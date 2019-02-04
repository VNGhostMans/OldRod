using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.Net.Cil;
using OldRod.Core.Architecture;
using OldRod.Core.Ast;

namespace OldRod.Core.Recompiler.ILTranslation
{
    public class PushRecompiler : IOpCodeRecompiler
    {
        public IList<CilInstruction> Translate(CompilerContext context, ILInstructionExpression expression)
        {
            var result = new List<CilInstruction>();
            
            switch (expression.OpCode.Code)
            {
                case ILCode.PUSHR_OBJECT:
                case ILCode.PUSHR_BYTE:
                case ILCode.PUSHR_WORD:
                case ILCode.PUSHR_DWORD:
                case ILCode.PUSHR_QWORD:
                    var variableEntry = context.Variables.First(x => x.Key.Name == expression.Operand.ToString());

                    result.Add(CilInstruction.Create(CilOpCodes.Ldloc, variableEntry.Value));
                    
                    var resultType = expression.OpCode.StackBehaviourPush.GetResultType();
                    if (variableEntry.Key.VariableType == VMType.Object)
                    {
                        result.Add(CilInstruction.Create(
                            resultType == VMType.Object ? CilOpCodes.Castclass : CilOpCodes.Unbox_Any,
                            context.ReferenceImporter.ImportType(resultType.ToMetadataType(context.TargetImage).ToTypeDefOrRef())));
                    }
                    break;
                
                case ILCode.PUSHI_DWORD:
                    result.Add(CilInstruction.Create(CilOpCodes.Ldc_I4, unchecked((int) (uint) expression.Operand)));
                    break;
                
                case ILCode.PUSHI_QWORD:
                    result.Add(CilInstruction.Create(CilOpCodes.Ldc_I8, unchecked((long) (ulong) expression.Operand)));
                    break;   
                
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return result;
        }
    }
}