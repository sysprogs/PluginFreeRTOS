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
}
