// Copyright (c) 2015, RTN Engenharia Ltda.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//     * Redistributions of source code must retain the above copyright
//       notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright
//       notice, this list of conditions and the following disclaimer in the
//       documentation and/or other materials provided with the distribution.
//     * Neither the name of the RTN Engenharia Ltda. nor the
//       names of its contributors may be used to endorse or promote products
//       derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL RTN ENGENHARIA LTDA. BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using VisualGDBExtensibility;

namespace PluginFreeRTOS
{
    enum CPUTypes
    {
        CPU_ARM_CORTEX_M0, CPU_ARM_CORTEX_M3, CPU_ARM_CORTEX_M4, CPU_UNIDENTIFIED
    };

    enum StackLayout
    {
        WithOptionalFPU,
        WithoutFPU
    }

    public class VirtualThreadProvider : IVirtualThreadProvider2
    {
        StackLayout? _StackLayout;

        public VirtualThreadProvider()
        {
        }
        
        CPUTypes? GetCPUType(IGlobalExpressionEvaluator e)
        {
            // Reads CPUID register in SCB
            ulong CPUID = (ulong)e.EvaluateIntegralExpression("*((int*)0xE000ED00)") & 0x0000FFF0;

            // Bits 15:4 are:
            // 0xC20 if Cortex-M0
            // 0xC23 if Cortex-M3
            // 0xC24 if Cortex-M4
            if (CPUID == 0x0000C200)
                return CPUTypes.CPU_ARM_CORTEX_M0;
            else if (CPUID == 0x0000C230)
                return CPUTypes.CPU_ARM_CORTEX_M3;
            else if (CPUID == 0x0000C240)
                return CPUTypes.CPU_ARM_CORTEX_M4;
            else
                return CPUTypes.CPU_UNIDENTIFIED;
        }

        List<IVirtualThread> GetThreadsFromList(IGlobalExpressionEvaluator e, string listName, UInt64? pxCurrentTCB, ref int currentThread)
        {
            List<IVirtualThread> threads = new List<IVirtualThread>();

            // Find how many tasks are there in this list
            int? threadsInListCount = (int?)e.EvaluateIntegralExpression(listName + ".uxNumberOfItems");

            // If none, abort early; also, it's possible that a given list doesn't exist --
            // for instance if INCLUDE_vTaskSuspend is 0
            if (!threadsInListCount.HasValue || threadsInListCount == 0)
                return threads;

            if (threadsInListCount < 0 || threadsInListCount > 4096)
                return threads; //We have most likely read something wrong. Cancel thread evaluation.

            // Get the first list item in that list, and the corresponding task control block
            UInt64 currentListItem = (UInt64)e.EvaluateIntegralExpression(listName + ".xListEnd.pxNext");
            UInt64 currentTCB = (UInt64)e.EvaluateIntegralExpression(string.Format("((ListItem_t*)0x{0:x})->pvOwner", currentListItem));

            for (int j = 0; j < threadsInListCount.Value; j++)
            {
                // Get stack pointer
                UInt64 savedSP = (UInt64)e.EvaluateIntegralExpression(string.Format("((TCB_t*)0x{0:x})->pxTopOfStack", currentTCB));

                // Get task name
                string name = e.EvaluateRawExpression(string.Format("((TCB_t*)0x{0:x})->pcTaskName", currentTCB));

                ulong? id = e.EvaluateIntegralExpression(string.Format("((TCB_t*)0x{0:x})->uxTCBNumber", currentTCB));
                int threadId;
                if (id.HasValue)
                    threadId = (int)id.Value;
                else
                    threadId = (int.MaxValue / 2) + currentThread;

                // Trim null characters (\000) and quotes from the name
                Regex rgx = new Regex("\\\"(.*)\\\"(.*)");
                name = name.Replace("\\000", "");
                name = rgx.Replace(name, "$1");

                // Add this thread to the list
                threads.Add(new VirtualThread(_StackLayout.Value, // CPU type
                                              name, // task name
                                              savedSP, // stack pointer
                                              threadId, // index
                                              pxCurrentTCB == currentTCB, // is currently running? (yes if equality holds)
                                              e)); // evaluator

                // Iterate to next list item and TCB
                currentListItem = (UInt64)e.EvaluateIntegralExpression(string.Format("((ListItem_t*)0x{0:x})->pxNext", currentListItem));
                currentTCB = (UInt64)e.EvaluateIntegralExpression(string.Format("((ListItem_t*)0x{0:x})->pvOwner", currentListItem));

                currentThread++;
            }

            return threads;
        }

        static readonly Regex rgDisassemblyLine = new Regex("[ \t=>]+0x[0-9a-fA-F]+[ \t]*<[^<>]*\\+[^<>]*>:");

        public IVirtualThread[] GetVirtualThreads(
            IGlobalExpressionEvaluator e)
        {
            // Get the total number of tasks running
            int taskCount = (int)e.EvaluateIntegralExpression("uxCurrentNumberOfTasks");

            // Get the number of different priorities (i.e. configMAX_PRIORITIES) --
            // there is a ready task list for each possible priority
            int priorityCount = (int)e.EvaluateIntegralExpression("sizeof(pxReadyTasksLists)/sizeof(pxReadyTasksLists[0])");

            List<IVirtualThread> threads = new List<IVirtualThread>();

            // Get a pointer to the current TCB -- it's used to compare to the TCB of
            // each list found below, and a match means it's the currently running task
            UInt64? pxCurrentTCB = e.EvaluateIntegralExpression("pxCurrentTCB");
            int currentTask = 0;

            // If the CPU type hasn't been found yet, do it -- it's necessary to find
            // out the stack layout later on

            if (!_StackLayout.HasValue)
            {
                int disassemblyLines = e.GDBSession.ExecuteCommand("disassemble PendSV_Handler").AdditionalOutput?.Count(s => rgDisassemblyLine.IsMatch(s)) ?? 0;
                if (disassemblyLines == 0)
                    disassemblyLines = e.GDBSession.ExecuteCommand("disassemble xPortPendSVHandler").AdditionalOutput?.Count(s => rgDisassemblyLine.IsMatch(s)) ?? 0;

                if (disassemblyLines >= 21 && disassemblyLines <= 22)
                    _StackLayout = StackLayout.WithoutFPU;
                else if (disassemblyLines >= 30 && disassemblyLines <= 33)
                    _StackLayout = StackLayout.WithOptionalFPU;
                else
                {
                    var cpu = GetCPUType(e);
                    _StackLayout = (cpu == CPUTypes.CPU_ARM_CORTEX_M4) ? StackLayout.WithOptionalFPU : StackLayout.WithoutFPU;
                }
            }

            // Find tasks in ready lists -- one for each possible priority in FreeRTOS
            for (int i = 0; i < priorityCount; i++)
            {
                // Add all tasks found in this list
                threads.AddRange(GetThreadsFromList(e, "pxReadyTasksLists[" + i + "]", pxCurrentTCB, ref currentTask));

                // If all tasks are accounted for, return early so as not to waste time
                // querying for extra tasks
                if (currentTask == taskCount)
                    return threads.ToArray();
            }

            // Find tasks in delayed task lists
            for (int i = 1; i <= 2; i++)
            {
                // Add all tasks found in this list
                threads.AddRange(GetThreadsFromList(e, "xDelayedTaskList" + i, pxCurrentTCB, ref currentTask));

                // If all tasks are accounted for, return early so as not to waste time
                // querying for extra tasks
                if (currentTask == taskCount)
                    return threads.ToArray();
            }

            // Find tasks in suspended task list
            threads.AddRange(GetThreadsFromList(e, "xSuspendedTaskList", pxCurrentTCB, ref currentTask));

            return threads.ToArray();
        }

        public int? GetActiveVirtualThreadId(IGlobalExpressionEvaluator evaluator)
        {
            return (int?)evaluator.EvaluateIntegralExpression("pxCurrentTCB->uxTCBNumber");
        }

        public IEnumerable CreateToolbarContents()
        {
            return null;
        }

        public void SetConfiguration(Dictionary<string, string> savedConfiguration)
        {
        }

        public Dictionary<string, string> GetConfigurationIfChanged()
        {
            return null;
        }
    }

    class VirtualThread : IVirtualThread
    {
        private UInt64 _SavedSP;
        private string _Name;
        int _UniqueID;
        IGlobalExpressionEvaluator _Evaluator;
        bool _IsRunning;
        private StackLayout _StackLayout;

        public VirtualThread(StackLayout stackLayout, string name, UInt64 savedSP, int id, bool isRunning,
                             IGlobalExpressionEvaluator evaluator)
        {
            _StackLayout = stackLayout;
            _SavedSP = savedSP;
            _Name = name;
            _UniqueID = id;
            _IsRunning = isRunning;
            _Evaluator = evaluator;
        }

        ulong GetSavedRegister(int slotNumber)
        {
            return _Evaluator.EvaluateIntegralExpression(
                string.Format("((void **)0x{0:x})[{1}]",
                              _SavedSP, slotNumber)).Value;
        }

        public IEnumerable<KeyValuePair<string, ulong>>
             GetSavedRegisters()
        {
            List<KeyValuePair<string, ulong>> result =
                new List<KeyValuePair<string, ulong>>();
            bool stackPadding = false;
            UInt64 savedSPCache;

            if (_IsRunning)
                return null;

            // Saved register order for ARM Cortex processors:
            // ARM Cortex-M0 and Cortex-M3: r4-r11,r0-r3,r12,r14,r15,xPSR
            // ARM Cortex-M4: r4-r11,r14,[s16-s31],r0-r3,r12,r14,r15,xPSR,[s0-s15,FPSCR]
            // (registers in brackets, for Cortex-M4, are only included if the FPU is in use)

            // This variable tracks how deep we are in the stack at any given point, i.e.,
            // the number of words already read from the stack
            int stackOffset;

            // List of registers in the order they are stacked
            string[] registers = { "r4", "r5", "r6", "r7", "r8", "r9", "r10", "r11", "r14" };
            string[] registers2 = { "r0", "r1", "r2", "r3", "r12", "r14", "r15", "cpsr" };

            // Get registers r4-r11 (the first stacked registers for all Cortex-M processors)
            // which are stacked in software inside the PendSV IRQ handler
            for (int stackPos = 0; stackPos < 8; stackPos++)
            {
                ulong val = GetSavedRegister(stackPos);
                result.Add(new KeyValuePair<string, ulong>(registers[stackPos],
                                                           val));
            }

            stackOffset = 8;

            ulong r14_exc = 0;

            if (_StackLayout == StackLayout.WithOptionalFPU)
            {
                // FreeRTOS stacks r14 after r11, but only for Cortex-M4; this is necessary
                // to check whether bit 4 is set, which controls whether FPU registers were
                // stacked or not
                r14_exc = GetSavedRegister(8);
                if ((r14_exc & 0x10) == 0)
                {
                    // FPU registers were stacked; grab them
                    for (int sReg = 16; sReg <= 31; sReg++)
                    {
                        ulong val = GetSavedRegister(sReg - 7);
                        result.Add(new KeyValuePair<string, ulong>("s" + sReg, val));
                    }

                    // Account for r14 and the 16 FPU registers
                    stackOffset += 17;
                }
                else
                {
                    // Account for r14
                    stackOffset++;
                }
            }

            // Get the registers stacked by the Cortex core hardware itself
            for (int stackPos = 0; stackPos < 8; stackPos++)
            {
                ulong val = GetSavedRegister(stackPos + stackOffset);
                result.Add(new KeyValuePair<string, ulong>(registers2[stackPos],
                                                           val));

                if (registers2[stackPos] == "cpsr")
                    // Check if xPSR bit 9 is set, which indicates a 4-byte
                    // padding word was pushed on the stack pointer
                    if ((val & 0x100) != 0)
                        stackPadding = true;
            }

            stackOffset += 8;

            if (_StackLayout == StackLayout.WithOptionalFPU)
            {
                // Again check whether bit 4 is set in r14; if it is, additional FPU registers
                // were stacked by the Cortex core
                if ((r14_exc & 0x10) == 0)
                {
                    // FPU registers were stacked; grab them
                    for (int sReg = 0; sReg <= 15; sReg++)
                    {
                        ulong val = GetSavedRegister(sReg + stackOffset);
                        result.Add(new KeyValuePair<string, ulong>("s" + sReg, val));
                    }

                    stackOffset += 16;
                }
            }

            // Update the stack pointer, accounting for the registers stacked
            // (by software and hardware) when the PendSV IRQ handler was called
            savedSPCache = _SavedSP + (ulong)(4 * stackOffset);

            // Account for padding in the stack, if it occurred in this case
            if (stackPadding)
            {
                savedSPCache += 4;
            }

            result.Add(new KeyValuePair<string, ulong>("r13", savedSPCache));

            return result;
        }

        public bool IsCurrentlyExecuting
        {
            get { return _IsRunning; }
        }

        public string Name
        {
            get { return _Name; }
        }

        public int UniqueID
        {
            get { return _UniqueID; }
        }
    }
}
