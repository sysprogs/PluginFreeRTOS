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
    public class FreeRTOSLiveWatchPlugin : ILiveWatchPlugin
    {
        public string UniqueID => "com.sysprogs.live.freertos";
        public string Name => "FreeRTOS";
        public LiveWatchNodeIcon Icon => LiveWatchNodeIcon.Thread;

        public ILiveWatchNodeSource CreateNodeSource(ILiveWatchEngine engine) => new NodeSource(engine);

        class NodeBase : ILiveWatchNode
        {
            protected NodeBase(string uniqueID)
            {
                UniqueID = uniqueID;
            }

            public string UniqueID { get; }
            public string RawType { get; protected set; }
            public string Name { get; protected set; }

            public LiveWatchCapabilities Capabilities { get; protected set; }

            public LiveWatchPhysicalLocation Location { get; protected set; }

            public virtual void Dispose()
            {
            }

            public virtual ILiveWatchNode[] GetChildren(LiveWatchChildrenRequestReason reason) => null;

            public virtual void SetSuspendState(LiveWatchNodeSuspendState state)
            {
            }

            public virtual void SetValueAsString(string newValue) => throw new NotSupportedException();
            public virtual LiveWatchNodeState UpdateState(LiveWatchUpdateContext context) => null;
        }

        class ScalarNodeBase : NodeBase, IScalarLiveWatchNode
        {
            protected ScalarNodeBase(string uniqueID)
                : base(uniqueID)
            {
                Capabilities |= LiveWatchCapabilities.CanSetBreakpoint | LiveWatchCapabilities.CanPlotValue;
            }

            public ILiveWatchFormatter[] SupportedFormatters { get; protected set; }
            public virtual ILiveWatchFormatter SelectedFormatter { get; set; }

            public virtual LiveVariableValue RawValue { get; protected set; }

            public virtual LiveWatchEnumValue[] EnumValues => null;

            public void SetEnumValue(LiveWatchEnumValue value)
            {
                throw new NotSupportedException();
            }
        }

        #region Threads
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

        enum ThreadListType
        {
            Ready,
            Delayed,
            Suspended,
            Running,
            Deleted,
        }
        #endregion

        #region Globals
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
        #endregion

        #region Queues

        class QueueListNode : NodeBase
        {
            private readonly NodeSource _Root;
            private QueueNode[] _Children;

            public QueueListNode(NodeSource root)
                : base("$rtos.queues")
            {
                _Root = root;
                Name = "Queues";
                Capabilities = LiveWatchCapabilities.CanHaveChildren | LiveWatchCapabilities.DoNotHighlightChangedValue;
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                var result = new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Queue };

                if (context.PreloadChildren)
                {
                    if (_Children == null)
                        _Children = _Root.GetAllQueues();
                    result.NewChildren = _Children;
                }

                return result;
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                base.SetSuspendState(state);
            }
        }

        enum QueueType  //The raw values must match the queueQUEUE_TYPE_XXX macros from queue.h
        {
            Invalid = 0,

            Queue = 1,
            Semaphore = 2,
            BinarySemaphore = 3,
            Mutex = 4,
        }

        class QueueNode : ScalarNodeBase
        {
            readonly ILiveWatchEngine _Engine;
            readonly QueueTypeDescriptor _Descriptor;
            readonly IPinnedVariableType _QueueType;
            private readonly NodeSource _Root;
            ILiveVariable _PointerVariable;
            IPinnedVariable _QueueVariable;

            struct Variables
            {
                public ulong LastKnownAddress;

                public ILiveVariable uxMessagesWaiting;
                public ILiveVariable uxLength;

                public ILiveVariable u_xSemaphore_xMutexHolder;
                public ILiveVariable u_xSemaphore_uxRecursiveCallCount;

                public ILiveVariable[] AllVariables => new[] { uxMessagesWaiting, uxLength, u_xSemaphore_xMutexHolder, u_xSemaphore_uxRecursiveCallCount }.Where(v => v != null).ToArray();

                public void Reset()
                {
                    uxMessagesWaiting?.Dispose();
                    uxMessagesWaiting = null;
                    uxLength?.Dispose();
                    uxLength = null;

                    u_xSemaphore_xMutexHolder?.Dispose();
                    u_xSemaphore_xMutexHolder = null;
                    u_xSemaphore_uxRecursiveCallCount?.Dispose();
                    u_xSemaphore_uxRecursiveCallCount = null;
                }
            }

            Variables _Variables;
            private ILiveWatchNode _QueueNode;

            WaitingThreadsNode _ReadThreadQueue, _WriteThreadQueue;

            class WaitingThreadsNode : ScalarNodeBase
            {
                public WaitingThreadsNode(string uniqueID)
                    : base(uniqueID)
                {
                }
            }

            public QueueNode(ILiveWatchEngine engine, NodeSource root, QueueTypeDescriptor queue, IPinnedVariable variable, IPinnedVariableType queueType, string userFriendlyName)
                : base("$rtos.queue." + variable.UserFriendlyName)
            {
                _Engine = engine;
                _Descriptor = queue;
                _QueueType = queueType;
                _Root = root;

                if (_Descriptor.IsIndirect)
                    _PointerVariable = engine.CreateLiveVariable(variable);
                else
                    _QueueVariable = variable;

                Name = userFriendlyName;
                RawType = _Descriptor.Type.ToString();
                Capabilities = LiveWatchCapabilities.CanHaveChildren | LiveWatchCapabilities.CanPlotValue | LiveWatchCapabilities.CanSetBreakpoint;
                SelectedFormatter = engine.CreateDefaultFormatter(ScalarVariableType.SInt32);
                Location = new LiveWatchPhysicalLocation(null, variable.SourceLocation.File, variable.SourceLocation.Line);
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                if (_PointerVariable != null)
                {
                    var address = _PointerVariable.GetValue().ToUlong();
                    if (address != _QueueVariable?.Address)
                    {
                        _QueueVariable = _Engine.Evaluator.CreateTypedVariable(address, _QueueType);
                    }
                }

                if (_QueueVariable != null && _QueueVariable.Address != _Variables.LastKnownAddress)
                {
                    _Variables.LastKnownAddress = _QueueVariable.Address;

                    //The previous instance will get auto-disposed by VisualGDB.
                    _QueueNode = null;
                    _Variables.Reset();

                    if (_QueueVariable.Address != 0)
                    {
                        _Variables.uxMessagesWaiting = _Engine.CreateLiveVariable(_QueueVariable.LookupSpecificChild(nameof(_Variables.uxMessagesWaiting)));
                        _Variables.uxLength = _Engine.CreateLiveVariable(_QueueVariable.LookupSpecificChild(nameof(_Variables.uxLength)));

                        _Variables.u_xSemaphore_xMutexHolder = _Engine.CreateLiveVariable(_QueueVariable.LookupChildRecursively("u.xSemaphore.xMutexHolder"));
                        _Variables.u_xSemaphore_uxRecursiveCallCount = _Engine.CreateLiveVariable(_QueueVariable.LookupChildRecursively("u.xSemaphore.uxRecursiveCallCount"));
                    }
                }

                if (context.PreloadChildren && _QueueVariable != null && _QueueNode == null && _QueueVariable.Address != 0)
                {
                    _QueueNode = _Engine.CreateNodeForPinnedVariable(_QueueVariable, new LiveWatchNodeOverrides { Name = "[Object]" });
                }

                var result = new LiveWatchNodeState();

                if (_QueueVariable.Address == 0)
                    result.Value = "[NULL]";
                else if (_Variables.uxLength == null || _Variables.uxMessagesWaiting == null)
                    result.Value = "???";
                else
                {
                    var rawValue = _Variables.uxMessagesWaiting.GetValue();
                    int value = (int)rawValue.ToUlong();
                    int maxValue = (int)_Variables.uxLength.GetValue().ToUlong();
                    ulong owner = 0, level = 0;

                    var detectedType = _Descriptor.Type;
                    if (detectedType != QueueType.Queue && _Variables.u_xSemaphore_xMutexHolder != null && _Variables.u_xSemaphore_uxRecursiveCallCount != null)
                    {
                        owner = _Variables.u_xSemaphore_xMutexHolder.GetValue().ToUlong();
                        level = _Variables.u_xSemaphore_uxRecursiveCallCount.GetValue().ToUlong();

                        if (owner == _QueueVariable.Address)
                            detectedType = QueueType.Semaphore;
                        else
                            detectedType = QueueType.Mutex;
                    }

                    if (detectedType == QueueType.Mutex)
                    {
                        if (value != 0)
                        {
                            result.Value = "free";
                            RawValue = new LiveVariableValue(rawValue.Timestamp, rawValue.Generation, BitConverter.GetBytes(0));
                        }
                        else if (_Variables.u_xSemaphore_xMutexHolder != null && _Variables.u_xSemaphore_uxRecursiveCallCount != null)
                        {
                            string threadName = _Root.GetThreadName(owner);
                            result.Value = $"taken by {threadName}";
                            if (level >= 1)
                                result.Value += $" (recursion = {level})";

                            RawValue = new LiveVariableValue(rawValue.Timestamp, rawValue.Generation, BitConverter.GetBytes((int)level + 1));
                        }
                        else
                            result.Value = "taken";
                    }
                    else
                    {
                        RawValue = rawValue;
                        result.Value = $"{value}/{maxValue}";
                    }

                    result.NewType = detectedType.ToString();

                    switch (detectedType)
                    {
                        case QueueType.BinarySemaphore:
                        case QueueType.Semaphore:
                        case QueueType.Mutex:
                            result.Icon = LiveWatchNodeIcon.Flag;
                            break;
                        case QueueType.Queue:
                            result.Icon = LiveWatchNodeIcon.Queue;
                            break;
                    }
                }

                if (context.PreloadChildren)
                {
                    result.NewChildren = new[] { _QueueNode }.Where(n => n != null).ToArray();
                }

                return result;
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                if (_PointerVariable != null)
                    _PointerVariable.SuspendUpdating = state.SuspendRegularUpdates;

                foreach (var v in _Variables.AllVariables)
                    v.SuspendUpdating = state.SuspendRegularUpdates;

                base.SetSuspendState(state);
            }

            public override void Dispose()
            {
                base.Dispose();
                _PointerVariable?.Dispose();

                foreach (var v in _Variables.AllVariables)
                    v.Dispose();
            }
        }

        struct QueueTypeDescriptor
        {
            public readonly QueueType Type;
            public readonly bool IsIndirect;
            public readonly string ContentsMemberName;

            public readonly string DiscardedNamePrefix;

            public QueueTypeDescriptor(QueueType type, bool isIndirect, string contentsMemberName = null, string discardedNamePrefix = null)
            {
                Type = type;
                IsIndirect = isIndirect;
                ContentsMemberName = contentsMemberName;
                DiscardedNamePrefix = discardedNamePrefix;
            }
        }

        static QueueTypeDescriptor ParseQueueType(string type)
        {
            switch (type)
            {
                case "osMutexId":
                    return new QueueTypeDescriptor(QueueType.Mutex, true);
                case "SemaphoreHandle_t":
                case "osSemaphoreId":
                    return new QueueTypeDescriptor(QueueType.Semaphore, true);
                case "osSemaphoreDef_t":
                case "os_semaphore_def":
                    return new QueueTypeDescriptor(QueueType.Semaphore, false, "controlblock", "os_semaphore_def_");
                case "osMutexDef_t":
                case "os_mutex_def":
                    return new QueueTypeDescriptor(QueueType.Semaphore, false, "controlblock", "os_mutex_def_");
                default:
                    return default;
            }
        }

        #endregion

        class NodeSource : ILiveWatchNodeSource
        {
            ILiveWatchEngine _Engine;
            readonly IPinnedVariableStructType _TCBType;
            readonly IPinnedVariableStructType _QueueType;

            class ThreadList : IDisposable
            {
                public readonly ThreadListType Type;
                public readonly ulong EndNodeAddress;
                public readonly ILiveVariable xListEnd_pxNext;

                readonly string _Name;

                ThreadList(ThreadListType type, ulong endNodeAddress, ILiveVariable xListEnd_pxNext, string name)
                {
                    Type = type;
                    EndNodeAddress = endNodeAddress;
                    this.xListEnd_pxNext = xListEnd_pxNext;
                    _Name = name;
                }

                public override string ToString() => _Name;

                public static void Locate(ILiveWatchEngine engine, IPinnedVariable baseVariable, ThreadListType type, List<ThreadList> result)
                {
                    if (baseVariable == null)
                        return;

                    var xListEnd = baseVariable?.LookupSpecificChild("xListEnd");
                    if (xListEnd == null)
                        return;

                    var xListEnd_pNext = xListEnd.LookupSpecificChild("pxNext");
                    if (xListEnd_pNext == null)
                        return;

                    ILiveVariable xListEnd_pNextLive = engine.CreateLiveVariable(xListEnd_pNext);
                    if (xListEnd_pNextLive == null)
                        return;

                    result.Add(new ThreadList(type, xListEnd.Address, xListEnd_pNextLive, baseVariable.UserFriendlyName));
                }

                public void Dispose()
                {
                    xListEnd_pxNext.Dispose();
                }

                public void Walk(NodeSource root, Dictionary<ulong, ThreadListType> result, HashSet<ulong> processedNodes, int totalThreadCountLimit, LiveVariableQueryMode queryMode)
                {
                    int threadsFound = 0;
                    ulong pxNext = 0;

                    for (var pListNode = xListEnd_pxNext.GetValue(queryMode).ToUlong(); pListNode != 0 && !processedNodes.Contains(pListNode); pListNode = pxNext, threadsFound++)
                    {
                        if (threadsFound >= totalThreadCountLimit)
                            break;

                        processedNodes.Add(pListNode);

                        try
                        {
                            var pTCB = pListNode - root.xStateListItem_Offset;

                            var pvOwner = root._Engine.LiveVariables.ReadMemory(pListNode + root.pvOwner_Offset, 4, queryMode).ToUlong();
                            pxNext = root._Engine.LiveVariables.ReadMemory(pListNode + root.pxNext_Offset, 4, queryMode).ToUlong();

                            if (pvOwner != pTCB)
                                continue;   //The list node doesn't point to the object itself anymore. Most likely, it has been freed and reused.

                            result[pTCB] = Type;
                        }
                        catch (Exception ex)
                        {
                            root._Engine.LogException(ex, $"failed to process TCB node at {pListNode}");
                            break;
                        }
                    }
                }
            }

            List<ThreadList> _AllThreadLists = new List<ThreadList>();  //e.g. xSuspendedTaskList
            readonly ILiveVariable _pxCurrentTCB, _uxCurrentNumberOfTasks;
            readonly uint xStateListItem_Offset, pvOwner_Offset, pxNext_Offset;

            private ILiveWatchNode[] _Children;

            public NodeSource(ILiveWatchEngine engine)
            {
                _Engine = engine;

                _TCBType = (IPinnedVariableStructType)engine.Evaluator.LookupType("TCB_t", true);
                _QueueType = (IPinnedVariableStructType)engine.Evaluator.LookupType("Queue_t");

                xStateListItem_Offset = _TCBType.LookupMember("xStateListItem", true).Offset;

                var listItemType = (IPinnedVariableStructType)engine.Evaluator.LookupType("ListItem_t", true);
                pvOwner_Offset = listItemType.LookupMember("pvOwner", true).Offset;
                pxNext_Offset = listItemType.LookupMember("pxNext", true).Offset;

                _pxCurrentTCB = engine.CreateLiveVariable("pxCurrentTCB", true);
                _uxCurrentNumberOfTasks = engine.CreateLiveVariable("uxCurrentNumberOfTasks", true);

                foreach (var pxReadyTaskList in engine.Evaluator.LookupVariable("pxReadyTasksLists")?.LookupChildren(0) ?? new IPinnedVariable[0])
                    ThreadList.Locate(engine, pxReadyTaskList, ThreadListType.Ready, _AllThreadLists);

                ThreadList.Locate(engine, engine.Evaluator.LookupVariable("xDelayedTaskList1"), ThreadListType.Delayed, _AllThreadLists);
                ThreadList.Locate(engine, engine.Evaluator.LookupVariable("xDelayedTaskList2"), ThreadListType.Delayed, _AllThreadLists);
                ThreadList.Locate(engine, engine.Evaluator.LookupVariable("xSuspendedTaskList"), ThreadListType.Suspended, _AllThreadLists);

                _Children = new ILiveWatchNode[] { new KernelNode(this, engine), new ThreadListNode(this), new QueueListNode(this) };
            }

            public void Dispose()
            {
                foreach (var threadList in _AllThreadLists)
                    threadList.Dispose();

                _pxCurrentTCB.Dispose();
                _uxCurrentNumberOfTasks.Dispose();
            }

            class ThreadLookup
            {
                HashSet<ulong> _ProcessedNodes = new HashSet<ulong>();

                private NodeSource _Root;

                public ThreadLookup(NodeSource root)
                {
                    _Root = root;
                    foreach (var list in _Root._AllThreadLists)
                        _ProcessedNodes.Add(list.EndNodeAddress);
                }

                Dictionary<ulong, ThreadListType> RunDiscoveryIteration(bool ignoreCachedMemoryValues, out int expectedThreads)
                {
                    LiveVariableQueryMode queryMode = ignoreCachedMemoryValues ? LiveVariableQueryMode.QueryDirectly : LiveVariableQueryMode.UseCacheIfAvailable;

                    Dictionary<ulong, ThreadListType> result = new Dictionary<ulong, ThreadListType>();
                    expectedThreads = (int)_Root._uxCurrentNumberOfTasks.GetValue(queryMode).ToUlong();
                    if (expectedThreads < 0 || expectedThreads > 4096)
                        throw new Exception("Unexpected FreeRTOS thread count: " + expectedThreads);

                    foreach (var list in _Root._AllThreadLists)
                    {
                        list.Walk(_Root, result, _ProcessedNodes, expectedThreads + 1, queryMode);
                    }

                    var pxCurrentTCB = _Root._pxCurrentTCB.GetValue(queryMode).ToUlong();
                    if (pxCurrentTCB != 0)
                        result[pxCurrentTCB] = ThreadListType.Running;

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

            Dictionary<ulong, ThreadNode> _CachedThreadNodes = new Dictionary<ulong, ThreadNode>();

            int _ThreadListGeneration;

            bool _SuspendThreadListUpdate;
            public bool SuspendThreadListUpdate
            {
                get => _SuspendThreadListUpdate;
                set
                {
                    _SuspendThreadListUpdate = value;
                    PropagateSuspensionFlags();
                }
            }

            private void PropagateSuspensionFlags()
            {
                bool suspendThreadUpdates = _SuspendThreadListUpdate;
                foreach (var lst in _AllThreadLists)
                    lst.xListEnd_pxNext.SuspendUpdating = suspendThreadUpdates;

                _pxCurrentTCB.SuspendUpdating = suspendThreadUpdates;
                _uxCurrentNumberOfTasks.SuspendUpdating = suspendThreadUpdates;
            }

            string ReadThreadName(ulong TCBAddress, out IPinnedVariable pTCB)
            {
                pTCB = _Engine.Evaluator.CreateTypedVariable(TCBAddress, _TCBType);

                var pcTaskName = pTCB.LookupSpecificChild("pcTaskName");
                string threadName = null;
                if (pcTaskName != null)
                {
                    try
                    {
                        var rawName = _Engine.ReadMemory(pcTaskName);
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

            public ThreadNode[] RefreshThreadList()
            {
                var foundThreads = new ThreadLookup(this).DiscoverAllThreads();
                int generation = Interlocked.Increment(ref _ThreadListGeneration);

                foreach (var thr in foundThreads)
                {
                    if (!_CachedThreadNodes.TryGetValue(thr.Key, out var threadObject))
                    {
                        string threadName = ReadThreadName(thr.Key, out var pTCB);
                        _CachedThreadNodes[thr.Key] = threadObject = new ThreadNode(_Engine, pTCB, threadName);
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

                foreach (var globalVar in _Engine.Evaluator.TopLevelVariables)
                {
                    var qt = ParseQueueType(globalVar.RawType.ToString());

                    if (qt.Type == QueueType.Invalid)
                        continue;

                    IPinnedVariable replacementVariable = null;

                    if (qt.ContentsMemberName != null)
                    {
                        var child = globalVar.LookupSpecificChild(qt.ContentsMemberName);
                        if (child == null)
                            continue;

                        replacementVariable = _Engine.Evaluator.CreateTypedVariable(child.Address, _QueueType);
                    }

                    string name = globalVar.UserFriendlyName;
                    if (name != null && qt.DiscardedNamePrefix != null && name.StartsWith(qt.DiscardedNamePrefix))
                        name = name.Substring(qt.DiscardedNamePrefix.Length);

                    discoveredQueues.Add(new QueueNode(_Engine, this, qt, replacementVariable ?? globalVar, _QueueType, name));
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
        }
    }

}
