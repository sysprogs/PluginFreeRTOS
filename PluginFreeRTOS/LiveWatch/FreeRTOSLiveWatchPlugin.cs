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
    }

}
