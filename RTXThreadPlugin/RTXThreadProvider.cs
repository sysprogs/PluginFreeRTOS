using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualGDBExtensibility;

namespace RTXThreadPlugin
{
    public class RTXThreadProvider : IVirtualThreadProvider2
    {
        public IEnumerable CreateToolbarContents()
        {
            return null;
        }

        public int? GetActiveVirtualThreadId(IGlobalExpressionEvaluator evaluator)
        {
            ulong? id = evaluator.EvaluateIntegralExpression("osRtxInfo.thread.run.curr");
            return (int?)id;
        }

        public void SetConfiguration(Dictionary<string, string> savedConfiguration)
        {
        }

        public Dictionary<string, string> GetConfigurationIfChanged()
        {
            return null;
        }

        void ReportThreadList(List<IVirtualThread> result, IGlobalExpressionEvaluator expressionEvaluator, string expression, HashSet<ulong> reportedIDs)
        {
            for (ulong? addr = expressionEvaluator.EvaluateIntegralExpression(expression); addr.HasValue && addr != 0; addr = expressionEvaluator.EvaluateIntegralExpression($"(({ThreadTypeName} *)0x{addr.Value:x8})->thread_next"))
            {
                if (reportedIDs.Contains(addr.Value))
                    continue;

                reportedIDs.Add(addr.Value);
                result.Add(new RTXThread(this, addr.Value, expressionEvaluator));
            }
        }

        public const string ThreadTypeName = "osRtxThread_t";

        public IVirtualThread[] GetVirtualThreads(IGlobalExpressionEvaluator expressionEvaluator)
        {
            //Based on the logic from svcRtxThreadEnumerate()

            List<IVirtualThread> result = new List<IVirtualThread>();

            var thr = expressionEvaluator.EvaluateIntegralExpression("osRtxInfo.thread.run.curr");
            if (!thr.HasValue || thr == 0)
                return new IVirtualThread[0];

            HashSet<ulong> reportedIDs = new HashSet<ulong> { thr.Value };
            result.Add(new RTXThread(this, thr.Value, expressionEvaluator, true));
            ReportThreadList(result, expressionEvaluator, "osRtxInfo.thread.ready.thread_list", reportedIDs);
            ReportThreadList(result, expressionEvaluator, "osRtxInfo.thread.delay_list", reportedIDs);
            ReportThreadList(result, expressionEvaluator, "osRtxInfo.thread.wait_list", reportedIDs);
            return result.ToArray();
        }

        enum KnownStackLayout
        {
            IntegralOnly,
            IntegralWithOptionalFP,
        }

        KnownStackLayout? _StackLayout;

        private KnownStackLayout GetThreadLayout(IGlobalExpressionEvaluator evaluator)
        {
            if (!_StackLayout.HasValue)
            {
                var insns = evaluator.DisassembleMemory("SVC_ContextSave", 10) ?? new SimpleInstruction[0];
                bool hasFP = false;
                //This is a basic check to distinguish between known stack layouts. It is not trying to actually reconstruct the stack layout by analyzing the disassembly.
                foreach (var insn in insns)
                {
                    if (insn.Text?.ToLower()?.Contains("vstmdbeq") == true)
                    {
                        hasFP = true;
                        break;
                    }

                    if (insn.Text?.ToLower()?.StartsWith("bl") == true)
                        break;
                }

                _StackLayout = hasFP ? KnownStackLayout.IntegralWithOptionalFP : KnownStackLayout.IntegralOnly;
            }

            return _StackLayout.Value;
        }

        class StackLayoutBuilder
        {
            struct RegisterRecord
            {
                public readonly string Name;
                public readonly int Offset;
                public readonly int Size;
                public int EndOffset => Offset + Size;

                public RegisterRecord(string name, int offset, int size)
                {
                    Name = name;
                    Offset = offset;
                    Size = size;
                }

                public override string ToString()
                {
                    return $"{Name} @{Offset}";
                }
            }

            List<RegisterRecord> _AllRegisters = new List<RegisterRecord>();
            int _Position;

            public void AddRegisters(string prefix, int first, int last, int sizePerRegister = 4)
            {
                for (int i = first; i <= last; i++)
                    AddSingleRegister(prefix + i, sizePerRegister);
            }

            public void AddSingleRegister(string name, int size = 4)
            {
                var reg = new RegisterRecord(name, _Position, size);
                _AllRegisters.Add(reg);
                _Position += size;
            }

            public void AddRegisters(params string[] registers)
            {
                foreach (var reg in registers)
                    AddSingleRegister(reg);
            }

            public void Skip(int bytes)
            {
                _Position += bytes;
            }

            public IEnumerable<KeyValuePair<string, ulong>> FetchValues(ulong sp, IGlobalExpressionEvaluator evaluator)
            {
                var bytes = evaluator.ReadMemoryBlock($"0x{sp:x8}", _Position) ?? new byte[0];
                List<KeyValuePair<string, ulong>> result = new List<KeyValuePair<string, ulong>>();
                foreach (var rec in _AllRegisters)
                {
                    if (rec.EndOffset > bytes.Length)
                        continue;   //Unavailable

                    switch (rec.Size)
                    {
                        case 4:
                            result.Add(new KeyValuePair<string, ulong>(rec.Name, BitConverter.ToUInt32(bytes, rec.Offset)));
                            break;
                        case 8:
                            result.Add(new KeyValuePair<string, ulong>(rec.Name, BitConverter.ToUInt64(bytes, rec.Offset)));
                            break;
                    }
                }

                result.Add(new KeyValuePair<string, ulong>("sp", sp + (ulong)_Position));
                return result;
            }
        }

        public class RTXThread : IVirtualThread2
        {
            readonly RTXThreadProvider _Provider;
            readonly ulong _ThreadObjectAddress;
            private readonly IGlobalExpressionEvaluator _Evaluator;

            public RTXThread(RTXThreadProvider provider, ulong threadObjectAddress, IGlobalExpressionEvaluator evaluator, bool isCurrentlyExecuting = false)
            {
                IsCurrentlyExecuting = isCurrentlyExecuting;
                _Provider = provider;
                _ThreadObjectAddress = threadObjectAddress;
                _Evaluator = evaluator;

                UniqueID = (int)threadObjectAddress;    //RTX threads don't have meaningful sequential IDs

                Name = evaluator.EvaluateStringExpression(GetFieldExpression("name")) ?? "(unnamed)";
            }

            private string GetFieldExpression(string fieldName) => $"(({ThreadTypeName} *)0x{_ThreadObjectAddress:x8})->{fieldName}";

            public string Priority { get; }

            public string Name { get; }

            public int UniqueID { get; }

            public bool IsCurrentlyExecuting { get; }


            public IEnumerable<KeyValuePair<string, ulong>> GetSavedRegisters()
            {
                var layout = _Provider.GetThreadLayout(_Evaluator);

                var sp = _Evaluator.EvaluateIntegralExpression(GetFieldExpression("sp")) ?? 0;
                var lr = _Evaluator.EvaluateIntegralExpression(GetFieldExpression("stack_frame")) ?? 0;
                if (sp == 0 || lr == 0)
                    return new KeyValuePair<string, ulong>[0];

                StackLayoutBuilder builder = new StackLayoutBuilder();
                builder.AddRegisters("r", 4, 11);
                if ((lr & 0x10) == 0)
                    builder.AddRegisters("s", 16, 31);

                builder.AddRegisters("r", 0, 3);
                builder.AddRegisters("r12", "lr", "pc");

                builder.Skip(4);
                return builder.FetchValues(sp, _Evaluator);
            }
        }
    }
}
