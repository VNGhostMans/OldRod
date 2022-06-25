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
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using OldRod.Core.Architecture;
using OldRod.Core.Ast.Cil;
using OldRod.Core.Ast.IL;

namespace OldRod.Core.Recompiler.IL
{
    public class PushRecompiler : IOpCodeRecompiler
    {
        public CilExpression Translate(RecompilerContext context, ILInstructionExpression expression)
        {
            switch (expression.OpCode.Code)
            {
                case ILCode.PUSHR_OBJECT:
                case ILCode.PUSHR_BYTE:
                case ILCode.PUSHR_WORD:
                case ILCode.PUSHR_DWORD:
                case ILCode.PUSHR_QWORD:
                    return CompileRegisterPush(context, expression);

                case ILCode.PUSHI_DWORD:
                    return new CilInstructionExpression(CilOpCodes.Ldc_I4,
                        unchecked((int) (uint) expression.Operand))
                    {
                        ExpressionType = context.TargetModule.CorLibTypeFactory.Int32
                    };
                
                case ILCode.PUSHI_QWORD:
                    return new CilInstructionExpression(CilOpCodes.Ldc_I8,
                        unchecked((long) (ulong) expression.Operand))
                    {
                        ExpressionType = context.TargetModule.CorLibTypeFactory.Int64
                    };

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static CilExpression CompileRegisterPush(RecompilerContext context, ILInstructionExpression expression)
        {
            var cilExpression = (CilExpression) expression.Arguments[0].AcceptVisitor(context.Recompiler);

            var resultType = expression.OpCode.StackBehaviourPush.GetResultType();

            if (cilExpression is CilUnboxToVmExpression)
            {
                // HACK: Unbox expressions unbox the value from the stack, but also convert it to their unsigned
                //       variant and box it again into an object. We need to unpack it again, however, we do not
                //       know the actual type of the value inside the box, as this is determined at runtime.
                //
                //       For now, we just make use of the Convert class provided by .NET, which works but would rather
                //       see a true "native" CIL conversion instead. 

                var corLibTypeFactory = context.TargetModule.CorLibTypeFactory;

                string methodName;
                TypeSignature returnType;
                switch (resultType)
                {
                    case VMType.Byte:
                        methodName = "ToByte";
                        returnType = corLibTypeFactory.Byte;
                        break;
                    case VMType.Word:
                        methodName = "ToUInt16";
                        returnType = corLibTypeFactory.UInt16;
                        break;
                    case VMType.Dword:
                        methodName = "ToUInt32";
                        returnType = corLibTypeFactory.UInt32;
                        break;
                    case VMType.Qword:
                        methodName = "ToUInt64";
                        returnType = corLibTypeFactory.UInt64;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                var convertTypeRef = new TypeReference(context.TargetModule, corLibTypeFactory.CorLibScope, "System", "Convert");
                var methodRef = new MemberReference(convertTypeRef, methodName,
                    MethodSignature.CreateStatic(returnType, corLibTypeFactory.Object));

                cilExpression.ExpectedType = corLibTypeFactory.Object;
                cilExpression = new CilInstructionExpression(
                    CilOpCodes.Call,
                    context.ReferenceImporter.ImportMethod(methodRef),
                    cilExpression);
            }

            if (resultType == VMType.Object)
            {
                if (cilExpression.ExpressionType.IsValueType)
                {
                    if (cilExpression is CilInstructionExpression instructionExpression
                        && instructionExpression.Instructions.Count == 1
                        && instructionExpression.Instructions[0].IsLdcI4()
                        && instructionExpression.Instructions[0].GetLdcI4Constant() == 0)
                    {
                        cilExpression = new CilInstructionExpression(CilOpCodes.Ldnull);
                    }
                    else
                    {
                        // If expression returns a value type, we have to box it to an object.
                        cilExpression = new CilInstructionExpression(CilOpCodes.Box,
                            context.ReferenceImporter.ImportType(cilExpression.ExpressionType.ToTypeDefOrRef()),
                            cilExpression);
                    }
                }
                else
                {
                    // Use the reference type of the expression instead of System.Object. 
                    cilExpression.ExpressionType = cilExpression.ExpressionType;
                }
            }
            
            if (cilExpression.ExpressionType == null)
                cilExpression.ExpressionType = resultType.ToMetadataType(context.TargetModule);

            return cilExpression;
        }
    }
}