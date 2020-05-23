using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualGDBExtensibility;

/*
 *  WARNING! This plugin has only been tested with the Nordic nRFConnect SDK and the targets supported by it.
 *  Using it on other Zephyr targets may require adjusting the stack layout definitions.
 */

namespace ZephyrThreadPlugin
{
    public class ZephyrThreadProvider : IVirtualThreadProvider2
    {
        public IEnumerable CreateToolbarContents() => null;

        public int? GetActiveVirtualThreadId(IGlobalExpressionEvaluator evaluator)
        {
            ulong? id = evaluator.EvaluateIntegralExpression("_kernel.current");
            return (int?)id;
        }

        public Dictionary<string, string> GetConfigurationIfChanged() => null;

        class ZephyrThread : IVirtualThread
        {
            public string Name { get; }

            public int UniqueID { get; }

            public bool IsCurrentlyExecuting { get; }

            private ZephyrThreadProvider _Provider;
            private ulong _ThreadObjectAddress;
            private IGlobalExpressionEvaluator _Evaluator;

            public IEnumerable<KeyValuePair<string, ulong>> GetSavedRegisters()
            {
                if (_Provider._CalleeSavedLayout == null)
                    return new KeyValuePair<string, ulong>[0];

                var result = _Provider._CalleeSavedLayout.Load(_Evaluator, _ThreadObjectAddress);

                var sp = result.FirstOrDefault(r => r.Key == "sp").Value;
                if (sp != 0 && _Provider._ESFLayout != null)
                {
                    var esfRegisters = _Provider._ESFLayout.Load(_Evaluator, sp);
                    result = result.Concat(esfRegisters);
                }

                return result;
            }

            public ZephyrThread(ZephyrThreadProvider provider, ulong threadObjectAddress, IGlobalExpressionEvaluator evaluator, bool isCurrentlyExecuting = false)
            {
                IsCurrentlyExecuting = isCurrentlyExecuting;
                _Provider = provider;
                _ThreadObjectAddress = threadObjectAddress;
                _Evaluator = evaluator;

                UniqueID = (int)threadObjectAddress;

                var name = evaluator.EvaluateStringExpression($"(char *)&(((struct k_thread *)0x{threadObjectAddress:x8})->name)");
                if (!string.IsNullOrEmpty(name))
                    Name = name;
                else
                {
                    var sym = evaluator.TryGetMeaningulSymbolName(threadObjectAddress);
                    if (sym.Symbol != null && sym.Offset == 0)
                        Name = sym.Symbol;
                    else
                        Name = $"0x{threadObjectAddress:x8}";
                }
            }
        }

        class LayoutCache
        {
            private int _MinOffset;
            private int _MaxOffset;
            private List<KeyValuePair<string, int>> _Offsets;

            public LayoutCache(int minOffset, int maxOffset, List<KeyValuePair<string, int>> offsets)
            {
                _MinOffset = minOffset;
                _MaxOffset = maxOffset;
                _Offsets = offsets;
            }

            const int WordSize = 4;

            public IEnumerable<KeyValuePair<string, ulong>> Load(IGlobalExpressionEvaluator evaluator, ulong address)
            {
                int blockSize = _MaxOffset - _MinOffset + WordSize;
                var data = evaluator.ReadMemoryBlock($"0x{address + (uint)_MinOffset:x8}", blockSize);
                List<KeyValuePair<string, ulong>> result = new List<KeyValuePair<string, ulong>>();

                foreach (var kv in _Offsets)
                {
                    int offset = kv.Value - _MinOffset;

                    if (data != null && data.Length >= (offset + WordSize))
                    {
                        result.Add(new KeyValuePair<string, ulong>(kv.Key, BitConverter.ToUInt32(data, offset)));
                    }
                    else
                    {
                        //We could not fetch the data block, or it was incomplete
                    }
                }

                return result;
            }
        }

        LayoutCache BuildLayoutCache(IGlobalExpressionEvaluator evaluator, string structName, IEnumerable<string> fieldDescriptors)
        {
            int minOffset = int.MaxValue, maxOffset = int.MinValue;
            List<KeyValuePair<string, int>> offsets = new List<KeyValuePair<string, int>>();

            foreach (var field in fieldDescriptors)
            {
                int idx = field.IndexOf('=');
                if (idx == -1)
                    continue;

                ulong? rawOffset = evaluator.EvaluateIntegralExpression($"&(({structName} *)0)->{field.Substring(0, idx).Trim()}");
                if (rawOffset.HasValue)
                {
                    int offset = (int)rawOffset.Value;

                    minOffset = Math.Min(minOffset, offset);
                    maxOffset = Math.Max(maxOffset, offset);
                    offsets.Add(new KeyValuePair<string, int>(field.Substring(idx + 1).Trim(), offset));
                }
            }

            if (offsets.Count == 0)
                return null;

            return new LayoutCache(minOffset, maxOffset, offsets);
        }

        LayoutCache _CalleeSavedLayout, _ESFLayout;

        public IVirtualThread[] GetVirtualThreads(IGlobalExpressionEvaluator expressionEvaluator)
        {
            _CalleeSavedLayout ??= BuildLayoutCache(expressionEvaluator, "struct k_thread", new[] { "callee_saved.psp=sp" }.Concat(Enumerable.Range(1, 8).Select(i => $"callee_saved.v{i}=r{i + 3}")));
            _ESFLayout ??= BuildLayoutCache(expressionEvaluator, "struct __esf", new[] { "basic.ip=ip", "basic.lr=lr", "basic.pc=pc" }.Concat(Enumerable.Range(1, 4).Select(i => $"basic.a{i}=r{i - 1}")));

            List<IVirtualThread> result = new List<IVirtualThread>();

            var pCurrentThread = expressionEvaluator.EvaluateIntegralExpression("_kernel.current");
            if (!pCurrentThread.HasValue || pCurrentThread == 0)
                return new IVirtualThread[0];

            HashSet<ulong> reportedIDs = new HashSet<ulong>();

            var allThreads = expressionEvaluator.EvaluateIntegralExpression("_kernel.threads");
            if (allThreads.HasValue && allThreads.Value != 0)
            {
                for (ulong? pThread = allThreads; (pThread ?? 0) != 0; pThread = expressionEvaluator.EvaluateIntegralExpression($"((struct k_thread *)0x{pThread.Value:x8})->next_thread"))
                {
                    if (reportedIDs.Contains(pThread.Value))
                        break;  //Avoid infinite loops
                    reportedIDs.Add(pThread.Value);
                    result.Add(new ZephyrThread(this, pThread.Value, expressionEvaluator, pThread.Value == pCurrentThread.Value));
                }
            }

            if (!reportedIDs.Contains(pCurrentThread.Value))
                result.Insert(0, new ZephyrThread(this, pCurrentThread.Value, expressionEvaluator, true));

            return result.ToArray();
        }

        public void SetConfiguration(Dictionary<string, string> savedConfiguration)
        {
        }
    }
}
