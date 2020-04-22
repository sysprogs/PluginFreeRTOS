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
}
