using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualGDBExtensibility.LiveWatch;

namespace PluginFreeRTOS.LiveWatch
{
    class HeapStructureNode : NodeBase
    {
        private readonly NodeSource _Root;
        private readonly IPinnedVariable _ucHeap;
        private readonly int _BlockHeaderSize;
        private readonly int _NextFieldOffset;
        private readonly int _SizeFieldOffset;

        class HeapMetricNode : ScalarNodeBase
        {
            private Func<ParsedHeapState, int> _Callback;
            private HeapStructureNode _HeapNode;

            public HeapMetricNode(HeapStructureNode heapNode, string idSuffix, string name, Func<ParsedHeapState, int> callback)
                : base(heapNode.UniqueID + idSuffix)
            {
                Name = name;
                _Callback = callback;
                _HeapNode = heapNode;
                SelectedFormatter = _HeapNode._Root.Engine.CreateDefaultFormatter(ScalarVariableType.SInt32);
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                int value = _Callback(_HeapNode._ParsedHeapContents);
                RawValue = new LiveVariableValue(_HeapNode._HeapContents.Timestamp, _HeapNode._HeapContents.Generation, BitConverter.GetBytes(value));
                return new LiveWatchNodeState { Value = SelectedFormatter.FormatValue(RawValue.Value) };
            }
        }

        class HeapBlockNode : NodeBase
        {
            private HeapStructureNode _HeapNode;
            private int _Index;

            public HeapBlockNode(HeapStructureNode heapNode, int index)
                : base($"{heapNode.UniqueID}[{index}]")
            {
                _HeapNode = heapNode;
                _Index = index;
            }

            public override LiveWatchPhysicalLocation Location
            {
                get
                {
                    var blocks = _HeapNode._ParsedHeapContents.Blocks;
                    if (blocks == null || _Index >= blocks.Length)
                        return default;
                    return new LiveWatchPhysicalLocation { Address = _HeapNode._ucHeap.Address + (uint)blocks[_Index].Offset };
                }
                protected set { }
            }

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                var blocks = _HeapNode._ParsedHeapContents.Blocks;
                if (blocks == null || _Index >= blocks.Length)
                    return new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Error, Value = "???" };

                var blk = blocks[_Index];

                return new LiveWatchNodeState
                {
                    Icon = blk.IsAllocated ? LiveWatchNodeIcon.Block : LiveWatchNodeIcon.EmptyBlock,
                    NewName = $"[0x{_HeapNode._ucHeap.Address + (uint)blk.Offset:x8}]",
                    NewType = $"{blk.Size} bytes",
                    Value = blk.IsAllocated ? _HeapNode.FormatBlockContents(blk) : "unallocated"
                };
            }
        }

        private string FormatBlockContents(HeapBlockInfo blk)
        {
            var data = _HeapContents.Value;
            if (data == null)
                return "???";

            StringBuilder result = new StringBuilder();
            for (int i = blk.Offset; i < (blk.Offset + blk.Size) && i < data.Length; i++)
            {
                result.AppendFormat("{0:x2} ", data[i]);
                if (result.Length > 16)
                {
                    result.Append("...");
                    break;
                }
            }

            return result.ToString();
        }

        class HeapBlockListNode : NodeBase
        {
            private HeapStructureNode _HeapNode;

            public HeapBlockListNode(HeapStructureNode heapNode)
                : base(heapNode.UniqueID + ".blocks")
            {
                Name = "[Heap Blocks]";
                _HeapNode = heapNode;
                Capabilities = LiveWatchCapabilities.CanHaveChildren;
            }

            List<HeapBlockNode> _CreatedNodes = new List<HeapBlockNode>();

            public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
            {
                var blocks = _HeapNode._ParsedHeapContents.Blocks;
                if (blocks == null || blocks.Length == 0)
                    return new LiveWatchNodeState { Value = $"[empty]" };
                else
                {
                    List<HeapBlockListNode> nodes = new List<HeapBlockListNode>();
                    var result = new LiveWatchNodeState { Value = $"[{blocks.Length} blocks]" };
                    if (context.PreloadChildren)
                    {
                        while (_CreatedNodes.Count < blocks.Length)
                            _CreatedNodes.Add(new HeapBlockNode(_HeapNode, _CreatedNodes.Count));

                        result.NewChildren = _CreatedNodes.Take(blocks.Length).ToArray();
                    }

                    return result;
                }
            }
        }

        ILiveVariable _LiveHeap;
        private LiveVariableValue _HeapContents;
        private ParsedHeapState _ParsedHeapContents;
        private ILiveWatchNode[] _Children;

        public HeapStructureNode(NodeSource root)
            : base("$rtos.heap")
        {
            _Root = root;

            _ucHeap = root.Engine.Symbols.LookupVariable("ucHeap");

            var blockLinkType = root.Engine.Symbols.LookupType("BlockLink_t");
            if (blockLinkType is IPinnedVariableStructType st)
            {
                _BlockHeaderSize = st.Size;
                _NextFieldOffset = (int?)st.LookupMember("pxNextFreeBlock", false)?.Offset ?? -1;
                _SizeFieldOffset = (int?)st.LookupMember("xBlockSize", false)?.Offset ?? -1;
            }

            Name = "Heap";
            Capabilities = LiveWatchCapabilities.CanHaveChildren | LiveWatchCapabilities.DoNotHighlightChangedValue;
        }

        public override void SetSuspendState(LiveWatchNodeSuspendState state)
        {
            base.SetSuspendState(state);
            if (_LiveHeap != null)
                _LiveHeap.SuspendUpdating = state.SuspendRegularUpdates;
        }

        struct HeapBlockInfo
        {
            public readonly int Offset;
            public readonly int Size;
            public readonly bool IsAllocated;

            public HeapBlockInfo(int offset, int size, bool isAllocated)
            {
                Offset = offset;
                Size = size;
                IsAllocated = isAllocated;
            }

            public override string ToString()
            {
                return (IsAllocated ? "Allocated" : "Free") + $" block with offset={Offset}, size={Size}";
            }
        }

        struct ParsedHeapState
        {
            public HeapBlockInfo[] Blocks;
            public string Error;
            public int TotalFreeBlocks, TotalUsedBlocks;
            public int TotalFreeSize, TotalUsedSize;
            public int MaxFreeBlock, MaxUsedBlock;
        }

        public override void Dispose()
        {
            base.Dispose();
            _LiveHeap?.Dispose();
        }

        ParsedHeapState ParseHeapContents(byte[] contents)
        {
            List<HeapBlockInfo> blocks = new List<HeapBlockInfo>();
            if (contents == null)
                return new ParsedHeapState { Error = "Cannot read heap contents" };

            ParsedHeapState result = new ParsedHeapState();

            ulong heapAddress = _ucHeap.Address;

            int offset = 0;

            while (offset <= (contents.Length - _BlockHeaderSize))
            {
                if (offset < 0)
                    offset = 0;

                uint pxNextFreeBlock = BitConverter.ToUInt32(contents, offset + _NextFieldOffset);
                uint xBlockSize = BitConverter.ToUInt32(contents, offset + _SizeFieldOffset);

                int increment = (int)(xBlockSize & 0x7FFFFFFF);
                if (increment <= 0)
                    break;

                var block = new HeapBlockInfo(offset + _BlockHeaderSize, increment - _BlockHeaderSize, (xBlockSize & 0x80000000U) != 0);
                blocks.Add(block);
                if (block.IsAllocated)
                {
                    result.TotalUsedBlocks++;
                    result.TotalUsedSize += block.Size;
                    result.MaxUsedBlock = Math.Max(result.MaxUsedBlock, block.Size);
                }
                else
                {
                    result.TotalFreeBlocks++;
                    result.TotalFreeSize += block.Size;
                    result.MaxFreeBlock = Math.Max(result.MaxUsedBlock, block.Size);
                }

                offset += increment;
            }

            if (offset != (contents.Length - _BlockHeaderSize))
                result.Error = $"Unexpected last block address (0x{_ucHeap.Address + (uint)offset} instead of 0x{_ucHeap.Address + (uint)(contents.Length - _BlockHeaderSize)})";

            result.Blocks = blocks.ToArray();
            return result;
        }

        public override LiveWatchNodeState UpdateState(LiveWatchUpdateContext context)
        {
            if (_ucHeap == null || _NextFieldOffset < 0 || _SizeFieldOffset < 0 || _BlockHeaderSize <= 0)
                return new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Error, Value = "Heap visualization only works with heap_4.c" };

            var result = new LiveWatchNodeState { Icon = LiveWatchNodeIcon.Heap };

            if (context.PreloadChildren)
            {
                if (_LiveHeap == null)
                    _LiveHeap = _Root.Engine.CreateLiveVariable(_ucHeap);

                _HeapContents = _LiveHeap.GetValue();
                _ParsedHeapContents = ParseHeapContents(_HeapContents.Value);

                if (_Children == null)
                {
                    _Children = new ILiveWatchNode[]
                    {
                        new HeapMetricNode(this, ".used.size", "Allocated Bytes", st => st.TotalUsedSize),
                        new HeapMetricNode(this, ".used.count", "Allocated Blocks", st => st.TotalUsedBlocks),
                        new HeapMetricNode(this, ".used.max", "Max. Allocation Size", st => st.MaxUsedBlock),
                        new HeapMetricNode(this, ".used.size", "Free Bytes", st => st.TotalFreeSize),
                        new HeapMetricNode(this, ".used.count", "Free Blocks", st => st.TotalFreeBlocks),
                        new HeapMetricNode(this, ".used.max", "Max. Free Block Size", st => st.MaxFreeBlock),
                        new HeapBlockListNode(this),
                    };
                }

                result.NewChildren = _Children;
                result.Value = $"{_ParsedHeapContents.TotalFreeSize} bytes available";
            }
            else
                result.Value = "(expand heap node to see details)";

            return result;
        }
    }

}
