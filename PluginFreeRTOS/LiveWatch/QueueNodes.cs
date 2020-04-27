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
    class QueueListNode : NodeBase
    {
        private readonly NodeSource _Root;
        private QueueNode[] _Children;

        public QueueListNode(NodeSource root)
            : base("$rtos.queues")
        {
            _Root = root;
            Name = "Synchronization Primitives";
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

        public static QueueTypeDescriptor ParseQueueType(string type)
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
                case "QueueHandle_t":
                case "osMessageQId":
                    return new QueueTypeDescriptor(QueueType.Queue, true);
                default:
                    return default;
            }
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
        private ILiveWatchNode _QueueObjectNode;

        WaitingThreadsNode _ReadThreadQueue, _WriteThreadQueue;

        public class WaitingThreadsNode : ScalarNodeBase
        {
            private QueueNode _Queue;
            private ThreadList _ThreadList;

            public readonly ulong QueueAddress;

            public WaitingThreadsNode(QueueNode queue, string idSuffix, string name, string memberName)
                : base(queue.UniqueID + idSuffix)
            {
                _Queue = queue;
                Name = name;

                QueueAddress = _Queue._QueueVariable.Address;
                var threadListVariable = _Queue._QueueVariable.LookupSpecificChild(memberName);
                _ThreadList = ThreadList.Locate(_Queue._Engine, threadListVariable, ThreadListType.Event, _Queue._Root.xEventListItem_Offset, true);
                _Queue._Root.OnQueueNodeSuspended(this, false);
                SelectedFormatter = _Queue._Engine.GetDefaultFormatter(ScalarVariableType.SInt32);
            }

            public override void SetSuspendState(LiveWatchNodeSuspendState state)
            {
                base.SetSuspendState(state);
                if (_ThreadList != null)
                    _ThreadList.SuspendUpdating = state.SuspendRegularUpdates;
                _Queue._Root.OnQueueNodeSuspended(this, state.SuspendRegularUpdates);
            }

            ulong[] RunSingleDiscoveryIteration(out int expectedCount, LiveVariableQueryMode queryMode = LiveVariableQueryMode.UseCacheIfAvailable)
            {
                expectedCount = (int)(_ThreadList?.uxNumberOfItems?.GetValue().ToUlong() ?? 0);
                if (expectedCount == 0)
                    return new ulong[0];

                Dictionary<ulong, ThreadListType> result = new Dictionary<ulong, ThreadListType>();
                HashSet<ulong> processedNodes = new HashSet<ulong>();
                _ThreadList.Walk(_Queue._Engine, result, processedNodes, expectedCount * 2, _Queue._Root.EventThreadListCache, queryMode);
                return result.Keys.ToArray();
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                if (_ThreadList == null)
                    return base.UpdateState(context);

                RawValue = _ThreadList.uxNumberOfItems.GetValue();

                LiveVariableQueryMode queryMode = LiveVariableQueryMode.UseCacheIfAvailable;
                ulong[] taskAddresses = null;
                bool mismatch = true;

                for (int iter = 0; iter < 3; iter++)
                {
                    taskAddresses = RunSingleDiscoveryIteration(out var expectedCount, queryMode);
                    if (taskAddresses.Length == expectedCount)
                    {
                        mismatch = false;
                        break;
                    }
                    queryMode = LiveVariableQueryMode.QueryDirectly;
                }

                string value = "---";
                if (taskAddresses.Length > 0)
                    value = string.Join(", ", taskAddresses.Select(_Queue._Root.GetThreadName).ToArray());

                if (mismatch)
                    value += " (imprecise)";

                return new LiveWatchNodeState
                {
                    Value = value,
                    Icon = LiveWatchNodeIcon.Thread,
                };
            }

            public override void Dispose()
            {
                base.Dispose();
                _ThreadList.Dispose();
                _Queue._Root.OnQueueNodeSuspended(this, true);
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
            SelectedFormatter = engine.GetDefaultFormatter(ScalarVariableType.SInt32);
            Location = new LiveWatchPhysicalLocation(null, variable.SourceLocation.File, variable.SourceLocation.Line);
        }

        public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
        {
            if (_PointerVariable != null)
            {
                var address = _PointerVariable.GetValue().ToUlong();
                if (address != _QueueVariable?.Address)
                {
                    _QueueVariable = _Engine.Symbols.CreateTypedVariable(address, _QueueType);
                }
            }

            if (_QueueVariable != null && _QueueVariable.Address != _Variables.LastKnownAddress)
            {
                _Variables.LastKnownAddress = _QueueVariable.Address;

                //The previous instance will get auto-disposed by VisualGDB.
                _QueueObjectNode = null;
                _Variables.Reset();

                if (_QueueVariable.Address != 0)
                {
                    _Variables.uxMessagesWaiting = _Engine.CreateLiveVariable(_QueueVariable.LookupSpecificChild(nameof(_Variables.uxMessagesWaiting)));
                    _Variables.uxLength = _Engine.CreateLiveVariable(_QueueVariable.LookupSpecificChild(nameof(_Variables.uxLength)));

                    _Variables.u_xSemaphore_xMutexHolder = _Engine.CreateLiveVariable(_QueueVariable.LookupChildRecursively("u.xSemaphore.xMutexHolder"));
                    _Variables.u_xSemaphore_uxRecursiveCallCount = _Engine.CreateLiveVariable(_QueueVariable.LookupChildRecursively("u.xSemaphore.uxRecursiveCallCount"));
                }
            }

            if (context.PreloadChildren && _QueueVariable != null && _QueueObjectNode == null && _QueueVariable.Address != 0)
            {
                _QueueObjectNode = _Engine.CreateNodeForPinnedVariable(_QueueVariable, new LiveWatchNodeOverrides { Name = "[Object]" });
            }

            var result = new LiveWatchNodeState();

            if ((_QueueVariable?.Address ?? 0) == 0)
                result.Value = "[NULL]";
            else if (_Variables.uxLength == null || _Variables.uxMessagesWaiting == null)
                result.Value = "???";
            else
            {
                var detectedType = _Descriptor.Type;
                var rawValue = _Variables.uxMessagesWaiting.GetValue();
                int value = (int)rawValue.ToUlong();
                int maxValue = (int)_Variables.uxLength.GetValue().ToUlong();
                ulong owner = 0, level = 0;

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


                if (context.PreloadChildren)
                {
                    ProvideWaitingThreadsNodes(detectedType);
                    result.NewChildren = new[] { _ReadThreadQueue, _WriteThreadQueue, _QueueObjectNode }.Where(n => n != null).ToArray();
                }
            }

            return result;
        }

        private void ProvideWaitingThreadsNodes(QueueType detectedType)
        {
            bool isActualQueue = detectedType == QueueType.Queue;

            if (_ReadThreadQueue?.QueueAddress != _QueueVariable.Address)
                _ReadThreadQueue = new WaitingThreadsNode(this, ".readers", isActualQueue ? "Waiting to Read" : "Waiting Threads", "xTasksWaitingToReceive");
            if (isActualQueue && _WriteThreadQueue?.QueueAddress != _QueueVariable.Address)
                _WriteThreadQueue = new WaitingThreadsNode(this, ".writers", "Waiting to Write", "xTasksWaitingToSend");
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
}
