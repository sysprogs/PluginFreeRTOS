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
        class StackUsageNode : ScalarNodeBase
        {
            private ThreadNode _ThreadNode;

            int _EstimatedStackSize;
            ulong _pxStack;
            bool _StackSizeQueried;

            void UpdateEstimatedStackSize()
            {
                if (_ThreadNode._Variables.pxStack != null && _ThreadNode._Variables.pxTopOfStack != null)
                {
                    _pxStack = _ThreadNode._Engine.ReadMemory(_ThreadNode._Variables.pxStack).ToUlong();
                    ulong pxTopOfStack = _ThreadNode._Variables.pxTopOfStack.GetValue().ToUlong();
                    if (_pxStack != 0 && pxTopOfStack != 0)
                    {
                        //The logic below will only work if the stack was allocated from the FreeRTOS heap (tested with heap_4).
                        uint heapBlockSize = (uint)_ThreadNode._Engine.LiveVariables.ReadMemory(_pxStack - 4, 4).ToUlong();
                        if ((heapBlockSize & 0x80000000) != 0)
                        {
                            _EstimatedStackSize = (int)(heapBlockSize & 0x7FFFFFFF) - 8;
                        }
                    }
                }
            }

            public StackUsageNode(ThreadNode threadNode)
                : base(threadNode.UniqueID + ".stack")
            {
                _ThreadNode = threadNode;
                Name = "Stack Usage";

                SelectedFormatter = _ThreadNode._Engine.CreateDefaultFormatter(ScalarVariableType.UInt32);
                Capabilities |= LiveWatchCapabilities.CanSetBreakpoint | LiveWatchCapabilities.CanPlotValue;
                _ThreadNode._Variables.pxTopOfStack.SuspendUpdating = false;
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                _ThreadNode._Variables.pxTopOfStack.SuspendUpdating = state.SuspendRegularUpdates;

                base.SetSuspendState(state);
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                var pxTopOfStack = _ThreadNode._Variables.pxTopOfStack.GetValue();

                if (!_StackSizeQueried)
                {
                    UpdateEstimatedStackSize();
                    _StackSizeQueried = true;
                }

                int stackUsage = (int)(pxTopOfStack.ToUlong() - _pxStack);
                RawValue = new LiveVariableValue(pxTopOfStack.Timestamp, pxTopOfStack.Generation, BitConverter.GetBytes(stackUsage));

                string text;
                if (_EstimatedStackSize > 0)
                    text = $"{stackUsage}/{_EstimatedStackSize} bytes";
                else
                    text = $"{stackUsage} bytes";

                return new LiveWatchNodeState
                {
                    Value = text
                };
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

                SelectedFormatter = _ThreadNode._Engine.CreateDefaultFormatter(ScalarVariableType.UInt8);
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

        ILiveWatchNode[] _Children;

        struct VariableCollection
        {
            public ILiveVariable pxTopOfStack;

            public IPinnedVariable uxBasePriority;
            public IPinnedVariable uxMutexesHeld;
            public IPinnedVariable pxStack;
        }

        VariableCollection _Variables;


        public ThreadNode(ILiveWatchEngine engine, IPinnedVariable pTCB, string threadName)
            : base("$rtos.thread." + threadName)
        {
            _Engine = engine;
            _TCB = pTCB;
            Name = threadName;

            _Variables.pxTopOfStack = engine.CreateLiveVariable(pTCB.LookupSpecificChild(nameof(_Variables.pxTopOfStack)), LiveVariableFlags.CreateSuspended);
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
                if (_Variables.pxStack != null && _Variables.pxTopOfStack != null)
                    nodes.Add(new StackUsageNode(this));

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
            _Variables.pxTopOfStack?.Dispose();
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
