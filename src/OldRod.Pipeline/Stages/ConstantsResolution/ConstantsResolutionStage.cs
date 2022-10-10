// Project OldRod - A KoiVM devirtualisation utility.
// Copyright (C) 2019 Washi
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using OldRod.Core.Architecture;

namespace OldRod.Pipeline.Stages.ConstantsResolution
{
    public class ConstantsResolutionStage : IStage
    {
        private const string Tag = "ConstantsResolver";

        public string Name => "Constants resolution stage";

        public void Run(DevirtualisationContext context)
        {
            if (context.Options.OverrideConstants)
            {
                context.Logger.Debug(Tag, "Using pre-defined constants.");
                context.Constants = context.Options.Constants;
            }
            else
            {
                context.Logger.Debug(Tag, "Attempting to auto-detect constants...");
                context.Constants = AutoDetectConstants(context);
            }

            context.Logger.Debug(Tag, "Attempting to extract key scalar value...");
            context.Constants.KeyScalar = FindKeyScalarValue(context);
        }

        private VMConstants AutoDetectConstants(DevirtualisationContext context)
        {
            bool rename = context.Options.RenameSymbols;

            var constants = new VMConstants();
            var fields = FindConstantFieldsAndValues(context);

            foreach (var field in fields)
                constants.ConstantFields.Add(field.Key, field.Value);

            // TODO:
            // We assume that the constants appear in the same order as they were defined in the original source code.
            // This means the metadata tokens of the fields are also in increasing order. However, this could cause
            // problems when a fork of the obfuscation tool is made which scrambles the order.  A more robust way of
            // matching should be done that is order agnostic.

            var sortedFields = fields
                .OrderBy(x => x.Key.MetadataToken.ToUInt32())
                .ToArray();

            int currentIndex = 0;

            context.Logger.Debug2(Tag, "Resolving register mapping...");
            for (int i = 0; i < (int) VMRegisters.Max; i++, currentIndex++)
            {
                constants.Registers.Add(sortedFields[currentIndex].Value, (VMRegisters) i);
                if (rename)
                    sortedFields[currentIndex].Key.Name = "REG_" + (VMRegisters) i;
            }

            context.Logger.Debug2(Tag, "Resolving flag mapping...");
            for (int i = 1; i < (int) VMFlags.Max; i <<= 1, currentIndex++)
            {
                constants.Flags.Add(sortedFields[currentIndex].Value, (VMFlags) i);
                if (rename)
                    sortedFields[currentIndex].Key.Name = "FLAG_" + (VMFlags) i;
            }

            context.Logger.Debug2(Tag, "Resolving opcode mapping...");
            for (int i = 0; i < (int) ILCode.Max; i++, currentIndex++)
            {
                constants.OpCodes.Add(sortedFields[currentIndex].Value, (ILCode) i);
                if (rename)
                    sortedFields[currentIndex].Key.Name = "OPCODE_" + (ILCode) i;
            }

            context.Logger.Debug2(Tag, "Resolving vmcall mapping...");
            for (int i = 0; i < (int) VMCalls.Max; i++, currentIndex++)
            {
                constants.VMCalls.Add(sortedFields[currentIndex].Value, (VMCalls) i);
                if (rename)
                    sortedFields[currentIndex].Key.Name = "VMCALL_" + (VMCalls) i;
            }

            context.Logger.Debug2(Tag, "Resolving helper init ID...");
            if (rename)
                sortedFields[currentIndex].Key.Name = "HELPER_INIT";
            constants.HelperInit = sortedFields[currentIndex++].Value;

            context.Logger.Debug2(Tag, "Resolving ECall mapping...");
            for (int i = 0; i < 4; i++, currentIndex++)
            {
                constants.ECallOpCodes.Add(sortedFields[currentIndex].Value, (VMECallOpCode) i);
                if (rename)
                    sortedFields[currentIndex].Key.Name = "ECALL_" + (VMECallOpCode) i;
            }

            context.Logger.Debug2(Tag, "Resolving function signature flags...");
            sortedFields[currentIndex].Key.Name = "FLAG_INSTANCE";
            constants.FlagInstance = sortedFields[currentIndex++].Value;

            context.Logger.Debug2(Tag, "Resolving exception handler types...");
            for (int i = 0; i < (int) EHType.Max; i++, currentIndex++)
            {
                constants.EHTypes.Add(sortedFields[currentIndex].Value, (EHType) i);
                if (rename)
                    sortedFields[currentIndex].Key.Name = "EH_" + (EHType) i;
            }

            return constants;
        }

        private IDictionary<FieldDefinition, byte> FindConstantFieldsAndValues(DevirtualisationContext context)
        {
            context.Logger.Debug(Tag, "Locating constants type...");
            var constantsType = LocateConstantsType(context);
            if (constantsType == null)
                throw new DevirtualisationException("Could not locate constants type!");
            context.Logger.Debug(Tag, $"Found constants type ({constantsType.MetadataToken}).");

            if (context.Options.RenameSymbols)
            {
                constantsType.Namespace = "KoiVM.Runtime.Dynamic";
                constantsType.Name = "Constants";
            }
            
            context.Logger.Debug(Tag, $"Resolving constants table...");
            return ParseConstantValues(constantsType);
        }

        private static TypeDefinition LocateConstantsType(DevirtualisationContext context)
        {
            TypeDefinition constantsType = null;
            
            if (context.Options.OverrideVMConstantsToken)
            {
                context.Logger.Debug(Tag, $"Using token {context.Options.VMConstantsToken} for constants type.");
                constantsType = (TypeDefinition) context.RuntimeModule.LookupMember(context.Options.VMConstantsToken.Value);
            }
            else
            {
                // Constants type contains a lot of public static byte fields, and only those byte fields. 
                // Therefore we pattern match on this signature, by finding the type with the most public
                // static byte fields.

                // It is unlikely that any other type has that many byte fields, although it is possible.
                // This could be improved later on.

                int max = 0;
                foreach (var type in context.RuntimeModule.Assembly.Modules[0].TopLevelTypes)
                {
                    // Optimisation: Check first count of all fields. We need at least the amount of opcodes of fields. 
                    if (type.Fields.Count < (int) ILCode.Max)
                        continue;
                    
                    // Count public static byte fields.
                    int byteFields = type.Fields.Count(x =>
                        x.IsPublic && x.IsStatic && x.Signature.FieldType.IsTypeOf("System", "Byte"));

                    if (byteFields == type.Fields.Count && max < byteFields)
                    {
                        constantsType = type;
                        max = byteFields;
                    }
                }
            }

            return constantsType;
        }

        private static IDictionary<FieldDefinition, byte> ParseConstantValues(TypeDefinition constantsType)
        {
            // .cctor initialises the fields using a repetition of the following sequence:
            //
            //     ldnull
            //     ldc.i4 x
            //     stfld constantfield
            //
            // We can simply go over each instruction and "emulate" the ldc.i4 and stfld instructions.

            var result = new Dictionary<FieldDefinition, byte>();
            var cctor = constantsType.GetStaticConstructor();
            if (cctor == null)
                throw new DevirtualisationException("Specified constants type does not have a static constructor.");

            byte nextValue = 0;
            foreach (var instruction in cctor.CilMethodBody.Instructions)
            {
                if (instruction.IsLdcI4())
                    nextValue = (byte) instruction.GetLdcI4Constant();
                else if (instruction.OpCode.Code == CilCode.Stfld || instruction.OpCode.Code == CilCode.Stsfld)
                    result[(FieldDefinition) instruction.Operand] = nextValue;
            }

            return result;
        }

        private static uint FindKeyScalarValue(DevirtualisationContext context) 
        {
            context.Logger.Debug(Tag, "Locating VMContext type...");
            var vmCtxType = LocateVmContextType(context);
            if (vmCtxType is null) 
            {
                context.Logger.Warning(Tag, "Could not locate VMContext type, using default scalar value!");
                return 7;
            }
            context.Logger.Debug(Tag, $"Found VMContext type ({vmCtxType.MetadataToken}).");
            
            if (context.Options.RenameSymbols)
            {
                vmCtxType.Namespace = "KoiVM.Runtime.Execution";
                vmCtxType.Name = "VMContext";
            }

            var readByteMethod = vmCtxType.Methods.First(x => x.Signature.ReturnType.IsTypeOf("System", "Byte"));

            if (context.Options.RenameSymbols)
                readByteMethod.Name = "ReadByte";
            
            var instructions = readByteMethod.CilMethodBody.Instructions;
            for (int i = 0; i < instructions.Count; i++) 
            {
                var instr = instructions[i];
                if (instr.IsLdcI4() && instructions[i + 1].OpCode.Code == CilCode.Mul)
                    return (uint)instr.GetLdcI4Constant();
            }

            context.Logger.Warning(Tag, "Could not locate scalar value, using default!");
            return 7;
        }

        private static TypeDefinition LocateVmContextType(DevirtualisationContext context) 
        {
            if (context.Options.OverrideVMContextToken)
            {
                context.Logger.Debug(Tag, $"Using token {context.Options.VMContextToken} for constants type.");
                return (TypeDefinition)context.RuntimeModule.LookupMember(context.Options.VMContextToken.Value);
            }
            
            for (int i = 0; i < context.RuntimeModule.TopLevelTypes.Count; i++) 
            {
                var type = context.RuntimeModule.TopLevelTypes[i];
                if (type.IsAbstract)
                    continue;
                if (type.Methods.Count < 2)
                    continue;
                if (type.Fields.Count < 5)
                    continue;
                if (type.Methods.Count(x => x.IsPublic && x.Signature.ReturnType.IsTypeOf("System", "Byte")) != 1)
                    continue;
                if (type.Fields.Count(x => x.IsPublic && x.IsInitOnly && x.Signature.FieldType is SzArrayTypeSignature) != 1)
                    continue;

                int foundArrays = 0;
                int foundLists = 0;
                for (int j = 0; j < type.Fields.Count; j++) 
                {
                    var field = type.Fields[j];
                    if (field.IsPublic && field.IsInitOnly) 
                    {
                        if (field.Signature.FieldType is GenericInstanceTypeSignature genericSig &&
                            genericSig.GenericType.IsTypeOf("System.Collections.Generic", "List`1"))
                            foundLists++;

                        if (field.Signature.FieldType is SzArrayTypeSignature arraySig && arraySig.BaseType.IsValueType)
                            foundArrays++;
                    }
                }

                if (foundArrays != 1 || foundLists != 2)
                    continue;

                return type;
            }

            return null;
        }
    }
}