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
    class NodeSource : ILiveWatchNodeSource
    {
        public readonly ILiveWatchEngine Engine;
        readonly IPinnedVariableStructType _TCBType;
        readonly IPinnedVariableStructType _QueueType;
        
        List<ThreadList> _AllThreadLists = new List<ThreadList>();  //e.g. xSuspendedTaskList
        public readonly LinkedListNodeCache StateThreadListCache, EventThreadListCache;
        public readonly ILiveVariable _pxCurrentTCB, _uxCurrentNumberOfTasks;
        public readonly uint xEventListItem_Offset;

        private ILiveWatchNode[] _Children;

        public NodeSource(ILiveWatchEngine engine)
        {
            Engine = engine;

            _TCBType = (IPinnedVariableStructType)engine.Symbols.LookupType("TCB_t", true);
            _QueueType = (IPinnedVariableStructType)engine.Symbols.LookupType("Queue_t");

            var xStateListItem_Offset = _TCBType.LookupMember("xStateListItem", true).Offset;
            xEventListItem_Offset = _TCBType.LookupMember("xEventListItem", true).Offset;

            var listItemType = (IPinnedVariableStructType)engine.Symbols.LookupType("ListItem_t", true);
            var pvOwner_Offset = (int)listItemType.LookupMember("pvOwner", true).Offset;
            var pxNext_Offset = (int)listItemType.LookupMember("pxNext", true).Offset;

            StateThreadListCache = new LinkedListNodeCache(engine, pvOwner_Offset, pxNext_Offset);
            EventThreadListCache = new LinkedListNodeCache(engine, pvOwner_Offset, pxNext_Offset);

            _pxCurrentTCB = engine.CreateLiveVariable("pxCurrentTCB", true);
            _uxCurrentNumberOfTasks = engine.CreateLiveVariable("uxCurrentNumberOfTasks", true);

            foreach (var pxReadyTaskList in engine.Symbols.LookupVariable("pxReadyTasksLists")?.LookupChildren(0) ?? new IPinnedVariable[0])
                ThreadList.Locate(_AllThreadLists, engine, pxReadyTaskList, ThreadListType.Ready, xStateListItem_Offset);

            ThreadList.Locate(_AllThreadLists, engine, engine.Symbols.LookupVariable("xDelayedTaskList1"), ThreadListType.Delayed, xStateListItem_Offset);
            ThreadList.Locate(_AllThreadLists, engine, engine.Symbols.LookupVariable("xDelayedTaskList2"), ThreadListType.Delayed, xStateListItem_Offset);
            ThreadList.Locate(_AllThreadLists, engine, engine.Symbols.LookupVariable("xSuspendedTaskList"), ThreadListType.Suspended, xStateListItem_Offset);

            _Children = new ILiveWatchNode[] { new KernelNode(this, engine), new ThreadListNode(this), new QueueListNode(this), new HeapStructureNode(this) };
        }

        public void Dispose()
        {
            foreach (var threadList in _AllThreadLists)
                threadList.Dispose();

            _pxCurrentTCB.Dispose();
            _uxCurrentNumberOfTasks.Dispose();

            StateThreadListCache.Dispose();
            EventThreadListCache.Dispose();
        }

        Dictionary<ulong, ThreadNode> _CachedThreadNodes = new Dictionary<ulong, ThreadNode>();

        int _ThreadListGeneration;

        public bool SuspendThreadListUpdate
        {
            set
            {
                foreach (var lst in _AllThreadLists)
                    lst.SuspendUpdating = value;

                _pxCurrentTCB.SuspendUpdating = value;
                _uxCurrentNumberOfTasks.SuspendUpdating = value;
                StateThreadListCache.SuspendUpdating = value;
            }
        }

        string ReadThreadName(ulong TCBAddress, out IPinnedVariable pTCB)
        {
            pTCB = Engine.Symbols.CreateTypedVariable(TCBAddress, _TCBType);

            var pcTaskName = pTCB.LookupSpecificChild("pcTaskName");
            string threadName = null;
            if (pcTaskName != null)
            {
                try
                {
                    var rawName = Engine.ReadMemory(pcTaskName);
                    threadName = rawName.ToNullTerminatedString();
                }
                catch
                {
                }
            }

            return threadName ?? $"0x{TCBAddress:x8}";
        }

        public string GetThreadName(ulong pTCB)
        {
            if (pTCB == 0)
                return "[NULL]";

            if (!_ActiveThreadNameDictionary.TryGetValue(pTCB, out var name))
                _ActiveThreadNameDictionary[pTCB] = name = ReadThreadName(pTCB, out var unused);

            return name;
        }

        bool _HeapQueried = false;
        private IPinnedVariable _ucHeap;

        public ThreadNode[] RefreshThreadList()
        {
            var foundThreads = new ThreadLookup(this, _AllThreadLists, true, StateThreadListCache).DiscoverAllThreads();
            int generation = Interlocked.Increment(ref _ThreadListGeneration);

            foreach (var thr in foundThreads)
            {
                if (!_HeapQueried)
                {
                    _HeapQueried = true;
                    _ucHeap = Engine.Symbols.LookupVariable("ucHeap");
                }

                if (!_CachedThreadNodes.TryGetValue(thr.Key, out var threadObject))
                {
                    string threadName = ReadThreadName(thr.Key, out var pTCB);
                    _CachedThreadNodes[thr.Key] = threadObject = new ThreadNode(Engine, pTCB, threadName, _ucHeap);
                }

                threadObject.UpdateLastSeenState(thr.Value, generation);
            }

            foreach (var kv in _CachedThreadNodes.ToArray())
            {
                kv.Value.MarkMissingIfNeeded(generation);

                if (kv.Value.IsMissingForLongerThan(1000))
                {
                    _CachedThreadNodes.Remove(kv.Key);  //VisualGDB will dispose the node once it realizes it's no longer reported as a child.
                }
            }

            return _CachedThreadNodes.Values.OrderBy(t => t.Name, StringComparer.InvariantCultureIgnoreCase).ToArray();
        }

        public QueueNode[] GetAllQueues()
        {
            List<QueueNode> discoveredQueues = new List<QueueNode>();

            foreach (var globalVar in Engine.Symbols.TopLevelVariables)
            {
                var qt = QueueListNode.ParseQueueType(globalVar.RawType.ToString());

                if (!qt.IsValid)
                    continue;

                IPinnedVariable replacementVariable = null;

                if (qt.ContentsMemberName != null)
                {
                    var child = globalVar.LookupSpecificChild(qt.ContentsMemberName);
                    if (child == null)
                        continue;

                    replacementVariable = Engine.Symbols.CreateTypedVariable(child.Address, _QueueType);
                }

                string name = globalVar.UserFriendlyName;
                if (name != null && qt.DiscardedNamePrefix != null && name.StartsWith(qt.DiscardedNamePrefix))
                    name = name.Substring(qt.DiscardedNamePrefix.Length);

                discoveredQueues.Add(new QueueNode(Engine, this, qt, replacementVariable ?? globalVar, _QueueType, name));
            }

            return discoveredQueues.ToArray();
        }

        public ILiveWatchNode[] PerformPeriodicUpdatesFromBackgroundThread()
        {
            //Nothing to do here. The actual updating is done when a specific node (e.g. thread list) is visible and expanded.
            return _Children;
        }

        Dictionary<ulong, string> _ActiveThreadNameDictionary = new Dictionary<ulong, string>();

        public string GetCurrentTaskName(ILiveVariable pxCurrentTCB)
        {
            //We use a separate live variable, so that we can suspend it independently. VisualGDB will automatically sort out the redundancies if both variables are enabled.
            return GetThreadName(pxCurrentTCB.GetValue().ToUlong());
        }

        HashSet<QueueNode.WaitingThreadsNode> _ActiveQueueNodes = new HashSet<QueueNode.WaitingThreadsNode>();

        public void OnQueueNodeSuspended(QueueNode.WaitingThreadsNode queueNode, bool suspended)
        {
            bool hasNodes;
            lock(_ActiveQueueNodes)
            {
                if (suspended)
                    _ActiveQueueNodes.Remove(queueNode);
                else
                    _ActiveQueueNodes.Add(queueNode);

                hasNodes = _ActiveQueueNodes.Count > 0;
            }

            EventThreadListCache.SuspendUpdating = !hasNodes;
        }
    }

    enum ThreadListType
    {
        Ready,
        Delayed,
        Suspended,
        Running,
        Deleted,

        Event,
    }
}
