using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using VisualGDBExtensibility;
using VisualGDBExtensibility.LiveWatch;

namespace PluginFreeRTOS.LiveWatch
{
    /// <summary>
    /// Encapsulates a single linked list of threads. Can be a part of a queue, or one of the global task lists.
    /// </summary>
    class ThreadList : IDisposable
    {
        public readonly ThreadListType Type;
        public readonly ulong EndNodeAddress;
        readonly ILiveVariable xListEnd_pxNext, uxNumberOfItems;
        private readonly uint _ListItemOffsetInTCB;
        readonly string _Name;

        public bool SuspendUpdating
        {
            set
            {
                xListEnd_pxNext.SuspendUpdating = value;

                if (uxNumberOfItems != null)
                    uxNumberOfItems.SuspendUpdating = value;
            }
        }


        ThreadList(ThreadListType type, ulong endNodeAddress, ILiveVariable xListEnd_pxNext, string name, ILiveVariable uxNumberOfItems, uint listItemOffsetInTCB)
        {
            Type = type;
            EndNodeAddress = endNodeAddress;
            this.xListEnd_pxNext = xListEnd_pxNext;
            this.uxNumberOfItems = uxNumberOfItems;
            _ListItemOffsetInTCB = listItemOffsetInTCB;
            _Name = name;
        }

        public override string ToString() => _Name;

        public static ThreadList Locate(ILiveWatchEngine engine, IPinnedVariable baseVariable, ThreadListType type, uint listItemOffsetInTCB, bool queryCountVariable = false)
        {
            if (baseVariable == null)
                return null;

            var xListEnd = baseVariable?.LookupSpecificChild("xListEnd");
            if (xListEnd == null)
                return null;

            var xListEnd_pNext = xListEnd.LookupSpecificChild("pxNext");
            if (xListEnd_pNext == null)
                return null;

            ILiveVariable xListEnd_pNextLive = engine.CreateLiveVariable(xListEnd_pNext);
            if (xListEnd_pNextLive == null)
                return null;

            ILiveVariable uxNumberOfItemsLive = null;
            if (queryCountVariable)
            {
                var uxNumberOfItems = baseVariable.LookupSpecificChild("uxNumberOfItems");
                if (uxNumberOfItems != null)
                    uxNumberOfItemsLive = engine.CreateLiveVariable(uxNumberOfItems);
            }

            return new ThreadList(type, xListEnd.Address, xListEnd_pNextLive, baseVariable.UserFriendlyName, uxNumberOfItemsLive, listItemOffsetInTCB);
        }

        public static void Locate(List<ThreadList> result, ILiveWatchEngine engine, IPinnedVariable baseVariable, ThreadListType type, uint listItemOffsetInTCB, bool queryCountVariable = false)
        {
            var list = Locate(engine, baseVariable, type, listItemOffsetInTCB, queryCountVariable);
            if (list != null)
                result.Add(list);
        }

        public void Dispose()
        {
            xListEnd_pxNext.Dispose();
        }

        public void Walk(NodeSource root,
                         Dictionary<ulong, ThreadListType> result,
                         HashSet<ulong> processedNodes,
                         int maxThreadsToLoad,
                         LinkedListNodeCache nodeCache,
                         LiveVariableQueryMode queryMode)
        {
            int threadsFound = 0;
            ulong pxNext = 0;

            for (var pListNode = xListEnd_pxNext.GetValue(queryMode).ToUlong(); pListNode != 0 && !processedNodes.Contains(pListNode); pListNode = pxNext, threadsFound++)
            {
                if (threadsFound >= maxThreadsToLoad)
                    break;

                processedNodes.Add(pListNode);

                try
                {
                    var pTCB = pListNode - _ListItemOffsetInTCB;

                    var cachedListNode = nodeCache.ProvideNode(pListNode);

                    cachedListNode.ReadValues(queryMode, out ulong pvOwner, out pxNext);

                    if (pvOwner != pTCB)
                    {
                        //The list node doesn't point to the object itself anymore. Most likely, it has been freed and reused.
                        cachedListNode.RemoveFromCache();
                        continue;   
                    }

                    result[pTCB] = Type;
                }
                catch (Exception ex)
                {
                    root.Engine.LogException(ex, $"failed to process TCB node at {pListNode}");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// This class tries to look up threads in multiple thread lists. This includes handling for the case where a thread is moved between the lists during an active lookup.
    /// </summary>
    class ThreadLookup
    {
        HashSet<ulong> _ProcessedNodes = new HashSet<ulong>();

        private NodeSource _Root;
        private readonly IEnumerable<ThreadList> _ThreadLists;
        private readonly bool _IncludeCurrentTCB;
        private readonly LinkedListNodeCache _Cache;

        public ThreadLookup(NodeSource root, IEnumerable<ThreadList> threadLists, bool includeCurrentTCB, LinkedListNodeCache cache)
        {
            _Root = root;
            _ThreadLists = threadLists;
            _IncludeCurrentTCB = includeCurrentTCB;
            _Cache = cache;
            foreach (var list in threadLists)
                _ProcessedNodes.Add(list.EndNodeAddress);
        }

        Dictionary<ulong, ThreadListType> RunDiscoveryIteration(bool ignoreCachedMemoryValues, out int expectedThreads)
        {
            LiveVariableQueryMode queryMode = ignoreCachedMemoryValues ? LiveVariableQueryMode.QueryDirectly : LiveVariableQueryMode.UseCacheIfAvailable;

            Dictionary<ulong, ThreadListType> result = new Dictionary<ulong, ThreadListType>();
            expectedThreads = (int)_Root._uxCurrentNumberOfTasks.GetValue(queryMode).ToUlong();
            if (expectedThreads < 0 || expectedThreads > 4096)
                throw new Exception("Unexpected FreeRTOS thread count: " + expectedThreads);

            foreach (var list in _ThreadLists)
            {
                list.Walk(_Root, result, _ProcessedNodes, expectedThreads + 1, _Cache, queryMode);
            }

            if (_IncludeCurrentTCB)
            {
                var pxCurrentTCB = _Root._pxCurrentTCB.GetValue(queryMode).ToUlong();
                if (pxCurrentTCB != 0)
                    result[pxCurrentTCB] = ThreadListType.Running;
            }

            return result;
        }

        public Dictionary<ulong, ThreadListType> DiscoverAllThreads()
        {
            Dictionary<ulong, ThreadListType> allFoundThreads = new Dictionary<ulong, ThreadListType>();

            for (int iter = 0; iter < 3; iter++)
            {
                //If we are reading the thread lists while the target is running, we may skip some threads that are just being moved between the lists.
                //We handle it it by requerying the list a few times, until the number of the discovered threads matches 'uxCurrentNumberOfTasks'.
                var foundThreads = RunDiscoveryIteration(iter > 0, out int expectedThreads);
                if (foundThreads.Count >= expectedThreads)
                    return foundThreads;

                foreach (var kv in foundThreads)
                    allFoundThreads[kv.Key] = kv.Value;

                if (allFoundThreads.Count == expectedThreads)
                    return allFoundThreads;
            }

            return allFoundThreads;
        }
    }

    /// <summary>
    /// Caches live variables for the linked list nodes of multiple threads. This allows batching the read requests, dramatically reducing the update time.
    /// </summary>
    class LinkedListNodeCache : IDisposable
    {
        public LinkedListNodeCache(ILiveWatchEngine engine, int pvOwner_Offset, int pxNext_Offset)
        {
            _Engine = engine;

            _Start = Math.Min(pvOwner_Offset, pxNext_Offset);
            _Length = Math.Max(pvOwner_Offset, pxNext_Offset) + 4;

            pvOwner_RelativeOffset = pvOwner_Offset - _Start;
            pxNext_RelativeOffset = pxNext_Offset - _Start;
        }

        Dictionary<ulong, Node> _Nodes = new Dictionary<ulong, Node>();
        private ILiveWatchEngine _Engine;

        readonly int _Start, _Length;
        readonly int pvOwner_RelativeOffset, pxNext_RelativeOffset;

        public bool SuspendUpdating
        {
            set
            {
                lock (_Nodes)
                    foreach (var node in _Nodes.Values)
                        node.SuspendUpdating = value;
            }
        }

        public class Node : IDisposable
        {
            private LinkedListNodeCache _Cache;
            private ulong _Address;

            ILiveVariable _Variable;

            public Node(LinkedListNodeCache linkedListNodeCache, ulong address)
            {
                _Cache = linkedListNodeCache;
                _Address = address;

                _Variable = _Cache._Engine.LiveVariables.CreateLiveVariable(address, _Cache._Length) ?? throw new Exception($"Failed to create live variable at 0x{address:x8}");
            }

            public bool SuspendUpdating
            {
                set
                {
                    _Variable.SuspendUpdating = value;
                }
            }

            public void Dispose()
            {
                _Variable?.Dispose();
                lock (_Cache._Nodes)
                    _Cache._Nodes.Remove(_Address);

            }

            public void ReadValues(LiveVariableQueryMode queryMode, out ulong pvOwner, out ulong pxNext)
            {
                var value = _Variable.GetValue(queryMode);
                if (!value.IsValid)
                    pvOwner = pxNext = 0;
                else
                {
                    pvOwner = BitConverter.ToUInt32(value.Value, _Cache.pvOwner_RelativeOffset);
                    pxNext = BitConverter.ToUInt32(value.Value, _Cache.pxNext_RelativeOffset);
                }
            }

            public void RemoveFromCache()
            {
                Dispose();
            }
        }

        public Node ProvideNode(ulong addrsss)
        {
            lock(_Nodes)
            {
                if (!_Nodes.TryGetValue(addrsss, out var node))
                    _Nodes[addrsss] = node = new Node(this, addrsss);
                return node;
            }
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
