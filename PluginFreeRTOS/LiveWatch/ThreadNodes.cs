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
    class ThreadListNode : NodeBase
    {
        private readonly NodeSource _Root;

        public ThreadListNode(NodeSource root)
            : base("$rtos.threads")
        {
            _Root = root;
            Name = "Threads";
            Capabilities = LiveWatchCapabilities.CanHaveChildren | LiveWatchCapabilities.DoNotHighlightChangedValue;
        }

        public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
        {
            var result = new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Thread };

            if (context.PreloadChildren)
                result.NewChildren = _Root.RefreshThreadList();

            return result;
        }

        public override void SetSuspendState(LiveWatchNodeSuspendState state)
        {
            _Root.SuspendThreadListUpdate = state.SuspendRegularUpdates;
            base.SetSuspendState(state);
        }
    }

    class ThreadNode : NodeBase
    {
        class StackUsageNodeBase : ScalarNodeBase
        {
            protected readonly ThreadNode _ThreadNode;

            int _EstimatedStackSize;
            ulong _pxStack;
            bool _StackSizeQueried;
            
            protected int ProvideEstimatedStackSize(out ulong pxStack)
            {
                if (!_StackSizeQueried)
                {
                    _StackSizeQueried = true;
                    if (_ThreadNode._Variables.pxStack != null && _ThreadNode._Variables.pxTopOfStack != null)
                    {
                        int pointerSize = _ThreadNode._Variables.pxStack.Size;
                        ulong allocatedMask = HeapStructureNode.GetBlockAllocatedMask(pointerSize >= 8);
                        _pxStack = _ThreadNode._Engine.ReadMemory(_ThreadNode._Variables.pxStack).ToUlong();
                        ulong pxTopOfStack = _ThreadNode._Variables.pxTopOfStackLive.GetValue().ToUlong();
                        if (_pxStack != 0 && pxTopOfStack != 0)
                        {
                            if (_ThreadNode._ucHeap != null && _pxStack >= _ThreadNode._ucHeap.Address && _pxStack < (_ThreadNode._ucHeap.Address + (uint)_ThreadNode._ucHeap.Size))
                            {
                                //The logic below will only work if the stack was allocated from the FreeRTOS heap (tested with heap_4).
                                ulong heapBlockSize = _ThreadNode._Engine.Memory.ReadMemory(_pxStack - (uint)pointerSize, pointerSize).ToUlong();
                                if ((heapBlockSize & allocatedMask) != 0)
                                {
                                    _EstimatedStackSize = (int)(heapBlockSize & ~allocatedMask) - 2 * pointerSize;
                                }
                            }
                            else
                            {
                                var fixedStackVariable = _ThreadNode._Engine.Symbols.TopLevelVariables.FirstOrDefault(v => v.Address == _pxStack && v.Size != 0);
                                if (fixedStackVariable != null)
                                    _EstimatedStackSize = fixedStackVariable.Size;
                            }
                        }
                    }
                }

                pxStack = _pxStack;
                return _EstimatedStackSize;
            }

            protected StackUsageNodeBase(ThreadNode thread, string idSuffix)
                : base(thread.UniqueID + idSuffix)
            {
                _ThreadNode = thread;
            }
        }

        class StackUsageNode : StackUsageNodeBase
        {
            public StackUsageNode(ThreadNode threadNode)
                : base(threadNode, ".stack")
            {
                Name = "Stack Usage";

                SelectedFormatter = _ThreadNode._Engine.GetDefaultFormatter(ScalarVariableType.UInt32);
                Capabilities |= LiveWatchCapabilities.CanSetBreakpoint | LiveWatchCapabilities.CanPlotValue;
                _ThreadNode._Variables.pxTopOfStackLive.SuspendUpdating = false;
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                _ThreadNode._Variables.pxTopOfStackLive.SuspendUpdating = state.SuspendRegularUpdates;

                base.SetSuspendState(state);
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                var pxTopOfStack = _ThreadNode._Variables.pxTopOfStackLive.GetValue();
                int estimatedStackSize = ProvideEstimatedStackSize(out var pxStack);

                long freeStackBytes = (long)(pxTopOfStack.ToUlong() - pxStack);

                //This will be negative if estimatedStackSize is unknown. It is by design to report that we don't know the exact size.
                RawValue = new LiveVariableValue(pxTopOfStack.Timestamp, pxTopOfStack.Generation, BitConverter.GetBytes(estimatedStackSize - freeStackBytes));

                if (estimatedStackSize == 0)
                    return new LiveWatchNodeState { Value = $"({freeStackBytes} bytes remaining)" };
                else
                    return new LiveWatchNodeState { Value = $"{estimatedStackSize - freeStackBytes}/{estimatedStackSize} bytes" };
            }
        }

        class HighestStackUsageNode : StackUsageNodeBase
        {
            ILiveVariable _BorderVariable;

            readonly uint _UnusedStackFillPatern;
            readonly int _MaxBorderVariableSize;
            bool _OverflowDetected, _PatternEverFound;

            public HighestStackUsageNode(ThreadNode threadNode)
                : base(threadNode, ".stack_highest")
            {
                Name = "Highest Stack Usage";

                _UnusedStackFillPatern = threadNode._Engine.Settings.UnusedStackFillPattern;
                _MaxBorderVariableSize = threadNode._Engine.Settings.StackBorderWatchSize;

                SelectedFormatter = _ThreadNode._Engine.GetDefaultFormatter(ScalarVariableType.UInt32);
                Capabilities |= LiveWatchCapabilities.CanSetBreakpoint | LiveWatchCapabilities.CanPlotValue;
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                base.SetSuspendState(state);
            }

            public override void Dispose()
            {
                base.Dispose();
            }

            int CountUnusedStackArea(byte[] data)
            {
                int offset = 0;
                while (offset < (data.Length - 3))
                {
                    uint value = BitConverter.ToUInt32(data, offset);
                    if (value != _UnusedStackFillPatern)
                        return offset;
                    offset += 4;
                }
                return offset;
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                int estimatedStackSize = ProvideEstimatedStackSize(out var pxStack);

                if (_OverflowDetected)
                    return ReportStackOverflow(estimatedStackSize);

                var rawValue = _BorderVariable?.GetValue() ?? default;

                if (!rawValue.IsValid || CountUnusedStackArea(rawValue.Value) != rawValue.Value.Length)
                {
                    int queriedStackSize;
                    if (_BorderVariable != null)
                        queriedStackSize = (int)(_BorderVariable.Address - pxStack);
                    else
                        queriedStackSize = (int)(_ThreadNode._Engine.ReadMemory(_ThreadNode._Variables.pxTopOfStack).ToUlong() - pxStack);

                    _BorderVariable?.Dispose();
                    _BorderVariable = null;

                    if (queriedStackSize < 0)
                        return new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Error, Value = $"Unexpected stack size ({queriedStackSize})" };

                    var data = _ThreadNode._Engine.Memory.ReadMemory(pxStack, queriedStackSize);
                    if (!data.IsValid)
                        return new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Error, Value = $"Failed to read stack contents (0x{pxStack:x8} - 0x{pxStack + (uint)queriedStackSize:x8})" };

                    int offset = CountUnusedStackArea(data.Value);

                    //We don't know whether it is a stack overflow, or if the empty stack is never filled with the pattern.
                    //We assume that if the stack appears overflown from the very beginning, the pattern is not being used at all.
                    _OverflowDetected = offset == 0;
                    if (offset != 0)
                        _PatternEverFound = true;

                    if (offset == 0)
                        return ReportStackOverflow(estimatedStackSize);
                    else
                    {
                        int watchSize = Math.Min(_MaxBorderVariableSize, offset);

                        _BorderVariable = _ThreadNode._Engine.Memory.CreateLiveVariable(pxStack + (uint)(offset - watchSize), watchSize, "Stack Border");
                    }
                }

                int freeStack = (int)(_BorderVariable.Address - pxStack) + _BorderVariable.Size;  /* The border variable watches the 1st free slot, not the 1st used one */
                int stackUsage = estimatedStackSize - freeStack;
                RawValue = new LiveVariableValue(rawValue.Timestamp, rawValue.Generation, BitConverter.GetBytes(stackUsage));

                string text;
                if (estimatedStackSize > 0)
                    text = $"{stackUsage}/{estimatedStackSize} bytes";
                else
                    text = $"{stackUsage} bytes";

                return new LiveWatchNodeState
                {
                    Value = text
                };
            }

            private LiveWatchNodeState ReportStackOverflow(int estimatedStackSize)
            {
                RawValue = new LiveVariableValue(DateTime.Now, LiveVariableValue.OutOfScheduleGeneration, BitConverter.GetBytes(estimatedStackSize));
                return new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Error, Value = _PatternEverFound ? "Stack overflow detected!" : $"Unused stack is not filled with 0x{_UnusedStackFillPatern}" };
            }
        }

        class IsRunningNode : ScalarNodeBase
        {
            private ThreadNode _ThreadNode;

            public IsRunningNode(ThreadNode threadNode)
                : base(threadNode.UniqueID + ".is_running")
            {
                _ThreadNode = threadNode;
                Name = "Currently Running";

                SelectedFormatter = _ThreadNode._Engine.GetDefaultFormatter(ScalarVariableType.UInt8);
            }

            static readonly byte[] True = new byte[] { 1 };
            static readonly byte[] False = new byte[] { 0 };

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                //This allows plotting the state of the thread as a regular int variable.
                RawValue = new LiveVariableValue(_ThreadNode._LastSeen, LiveVariableValue.OutOfScheduleGeneration, _ThreadNode.IsRunning ? True : False);

                return new LiveWatchNodeState
                {
                    Value = _ThreadNode.IsRunning ? "1" : "0"
                };
            }
        }

        private readonly ILiveWatchEngine _Engine;
        readonly IPinnedVariable _TCB;
        private readonly IPinnedVariable _ucHeap;
        ILiveWatchNode[] _Children;

        struct VariableCollection
        {
            public ILiveVariable pxTopOfStackLive;

            public IPinnedVariable uxBasePriority;
            public IPinnedVariable uxMutexesHeld;
            public IPinnedVariable pxStack;
            public IPinnedVariable pxTopOfStack;
        }

        VariableCollection _Variables;

        public ThreadNode(ILiveWatchEngine engine, IPinnedVariable pTCB, string threadName, IPinnedVariable ucHeap)
            : base("$rtos.thread." + threadName)
        {
            _Engine = engine;
            _TCB = pTCB;
            _ucHeap = ucHeap;
            Name = threadName;

            _Variables.pxTopOfStack = pTCB.LookupSpecificChild(nameof(_Variables.pxTopOfStack));
            _Variables.pxTopOfStackLive = engine.CreateLiveVariable(_Variables.pxTopOfStack, LiveVariableFlags.CreateSuspended);
            _Variables.uxBasePriority = pTCB.LookupSpecificChild(nameof(_Variables.uxBasePriority));
            _Variables.uxMutexesHeld = pTCB.LookupSpecificChild(nameof(_Variables.uxMutexesHeld));
            _Variables.pxStack = pTCB.LookupSpecificChild(nameof(_Variables.pxStack));

            Capabilities = LiveWatchCapabilities.CanHaveChildren;
        }

        public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
        {
            return new LiveWatchNodeState
            {
                Value = _LastSeenList.ToString(),
                Icon = LiveWatchNodeIcon.Thread,
            };
        }

        public override ILiveWatchNode[] GetChildren(LiveWatchChildrenRequestReason reason)
        {
            if (_Children == null)
            {
                List<ILiveWatchNode> nodes = new List<ILiveWatchNode>();
                nodes.Add(new IsRunningNode(this));
                if (_Variables.pxStack != null && _Variables.pxTopOfStackLive != null)
                    nodes.Add(new StackUsageNode(this));
                if (_Variables.pxStack != null && _Variables.pxTopOfStack != null)
                    nodes.Add(new HighestStackUsageNode(this));

                _Children = nodes.Concat(new ILiveWatchNode[]
                {
                        _Engine.CreateNodeForPinnedVariable(_Variables.uxBasePriority, new LiveWatchNodeOverrides { UniqueID = UniqueID + ".priority", Name = "Priority"}),
                        _Engine.CreateNodeForPinnedVariable(_Variables.uxMutexesHeld, new LiveWatchNodeOverrides { UniqueID = UniqueID + ".mutexes", Name = "Owned Mutexes"}),
                        _Engine.CreateNodeForPinnedVariable(_TCB, new LiveWatchNodeOverrides { UniqueID = UniqueID + ".tcb", Name = "[Raw TCB]"})
                }).Where(c => c != null).ToArray();
            }
            return _Children;
        }

        public override void Dispose()
        {
            _Variables.pxTopOfStackLive?.Dispose();
        }

        DateTime _LastSeen = DateTime.Now;
        ThreadListType _LastSeenList;
        int _LastSeenGeneration;

        public void UpdateLastSeenState(ThreadListType listType, int generation)
        {
            _LastSeen = DateTime.Now;
            _LastSeenList = listType;
            _LastSeenGeneration = generation;
        }

        public void MarkMissingIfNeeded(int generation)
        {
            if (generation != _LastSeenGeneration)
                _LastSeenList = ThreadListType.Deleted;
        }

        public bool IsRunning => _LastSeenList == ThreadListType.Running;

        public bool IsMissingForLongerThan(int msec) => _LastSeenList == ThreadListType.Deleted && (DateTime.Now - _LastSeen).TotalMilliseconds > msec;
    }

}
