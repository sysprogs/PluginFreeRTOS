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
    class KernelNode : NodeBase
    {
        class CurrentTaskNode : NodeBase
        {
            private ILiveVariable _pxCurrentTCB;
            private NodeSource _Root;

            public CurrentTaskNode(NodeSource root, ILiveVariable pxCurrentTCB)
                : base("$rtos.kernel.current_task")
            {
                _pxCurrentTCB = pxCurrentTCB;
                _Root = root;
                Name = "Current Task";
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                return new LiveWatchNodeState
                {
                    Value = _Root.GetCurrentTaskName(_pxCurrentTCB)
                };
            }

            public override void Dispose()
            {
                base.Dispose();
                _pxCurrentTCB.Dispose();
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                base.SetSuspendState(state);
                _pxCurrentTCB.SuspendUpdating = state.SuspendDirectValueUpdates;
            }
        }

        enum HeapNodeType
        {
            Current,
            Max
        }

        class HeapUsageNode : ScalarNodeBase
        {
            private ILiveVariable _Variable;
            private int _HeapSize;

            public HeapUsageNode(ILiveWatchEngine engine, ILiveVariable variable, HeapNodeType type, int heapSize)
                : base("$rtos.kernel.heap_current")
            {
                _Variable = variable;
                _HeapSize = heapSize;
                SelectedFormatter = engine.CreateDefaultFormatter(ScalarVariableType.SInt32);

                if (type == HeapNodeType.Current)
                    Name = "Heap Usage";
                else
                    Name = "Max. Heap Usage";
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                var freeBytesValue = _Variable.GetValue();
                var freeBytes = (int)freeBytesValue.ToUlong();

                int usedBytes = _HeapSize - freeBytes;

                RawValue = new LiveVariableValue(freeBytesValue.Timestamp, freeBytesValue.Generation, BitConverter.GetBytes(usedBytes));

                return new LiveWatchNodeState
                {
                    Value = $"{usedBytes}/{_HeapSize} bytes"
                };
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                base.SetSuspendState(state);
                _Variable.SuspendUpdating = state.SuspendDirectValueUpdates;
            }

            public override void Dispose()
            {
                base.Dispose();
                _Variable.Dispose();
            }
        }

        private readonly NodeSource _Root;
        private readonly ILiveWatchEngine _Engine;
        readonly ILiveVariable _xSchedulerRunning;

        ILiveWatchNode[] _Children;

        public KernelNode(NodeSource root, ILiveWatchEngine engine)
            : base("$rtos.kernel")
        {
            _Root = root;
            _Engine = engine;
            _xSchedulerRunning = engine.CreateLiveVariable("xSchedulerRunning", false);

            Name = "Kernel";
            Capabilities = LiveWatchCapabilities.CanHaveChildren;
        }

        public override ILiveWatchNode[] GetChildren(LiveWatchChildrenRequestReason reason)
        {
            if (_Children == null)
            {
                List<ILiveWatchNode> children = new List<ILiveWatchNode>();
                children.Add(_Engine.CreateNodeForPinnedVariable(_Engine.Evaluator.LookupVariable("uxCurrentNumberOfTasks"), new LiveWatchNodeOverrides { Name = "Total Tasks" }));
                children.Add(_Engine.CreateNodeForPinnedVariable(_Engine.Evaluator.LookupVariable("xTickCount"), new LiveWatchNodeOverrides { Name = "Tick Count" }));

                var pxCurrentTCB = _Engine.CreateLiveVariable("pxCurrentTCB", false);
                if (pxCurrentTCB != null)
                    children.Add(new CurrentTaskNode(_Root, pxCurrentTCB));

                var heapVar = _Engine.Evaluator.LookupVariable("ucHeap");
                if (heapVar != null)
                {
                    int heapSize = heapVar.Size;
                    var freeBytesVar = _Engine.CreateLiveVariable("xFreeBytesRemaining", false);
                    if (freeBytesVar != null)
                        children.Add(new HeapUsageNode(_Engine, freeBytesVar, HeapNodeType.Current, heapSize));

                    freeBytesVar = _Engine.CreateLiveVariable("xMinimumEverFreeBytesRemaining", false);
                    if (freeBytesVar != null)
                        children.Add(new HeapUsageNode(_Engine, freeBytesVar, HeapNodeType.Max, heapSize));
                }

                _Children = children.Where(c => c != null).ToArray();
            }

            return _Children;
        }

        public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
        {
            var result = new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Graph };

            if (_xSchedulerRunning != null)
            {
                if (_xSchedulerRunning.GetValue().ToUlong() != 0)
                    result.Value = "active";
                else
                    result.Value = "inactive";
            }

            return result;
        }

        public override void SetSuspendState(LiveWatchNodeSuspendState state)
        {
            if (_xSchedulerRunning != null)
                _xSchedulerRunning.SuspendUpdating = state.SuspendDirectValueUpdates;

            base.SetSuspendState(state);
        }

        public override void Dispose()
        {
            _xSchedulerRunning?.Dispose();
            base.Dispose();
        }
    }

}
