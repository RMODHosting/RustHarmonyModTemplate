#nullable enable
using Harmony;
using Harmony.ILCopying;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Radium.Core
{

    #region Arguments
    public enum ArgType
    {
        This,
        Local,
        Parameter,
        Field,
        Value,
        Call,
        Result
    }

    public abstract class Arg
    {
        public Type? Type;
        public ArgType SearchType;  
    }

    public class ArgResult : Arg
    {
        public ArgResult()
        {
            Type = null;
            SearchType = ArgType.Result;
        }
    }

    public class ArgThis: Arg
    {
        public ArgThis()
        {
            Type = null;
            SearchType = ArgType.This;
        }
    }

    public class ArgLocal : Arg
    {
        public int? Index;

        public ArgLocal(Type argType)
        {
            Type = argType;
            SearchType = ArgType.Local;
        }

        public ArgLocal(Type argType, int index)
        {
            Index = index;
            Type = argType;
            SearchType = ArgType.Local;
        }
    }

    /// <summary>
    /// ArgType is not technically required for parameters, 
    /// but will throw an error if the code breaks and it doesn't end up matching.
    /// </summary>
    public class ArgParameter : Arg
    {
        public int Index = -1;

        public ArgParameter(int index, Type argType)
        {
            if (index < 0)
                throw new ArgumentException("Index was invalid");

            Index = index;
            Type = argType;
            SearchType = ArgType.Parameter;
        }
    }


    public class ArgField : Arg
    {
        public FieldInfo Field;
        public ArgField(Type target, string field)
        {
            Field = AccessTools.Field(target, field);
             
            Type = null;
            SearchType = ArgType.Field;
        }
    }


    // public class ArgCall : Arg
    // {
    //     public Hook.Target TargetMethod;
    //     public Hook.Parameters Parameters;
    //
    //     public ArgCall(Type targetType, Hook.Target targetmethod, Hook.Parameters parameters)
    //     {
    //         TargetMethod = targetmethod;
    //         Parameters = parameters;
    //         Type = targetType;
    //         SearchType = ArgType.Call;
    //     }
    // }

    public class ArgValue : Arg
    {
        public object Value;

        public ArgValue(object val)
        {
            Value = val;
            SearchType = ArgType.Value;
        }
    }
    #endregion


    public class CodeMatch
    {
        /// <summary>The name of the match</summary>
        public string name;

        /// <summary>The matched opcodes</summary>
        public List<OpCode> opcodes = new List<OpCode>();

        /// <summary>The matched operands</summary>
        public List<object> operands = new List<object>();

        /// <summary>The matched labels</summary>
        public List<Label> labels = new List<Label>();

        /// <summary>The matched blocks</summary>
        public List<ExceptionBlock> blocks = new List<ExceptionBlock>();

        /// <summary>The jumps from the match</summary>
        public List<int> jumpsFrom = new List<int>();

        /// <summary>The jumps to the match</summary>
        public List<int> jumpsTo = new List<int>();

        /// <summary>The match predicate</summary>
        public Func<CodeInstruction, bool> predicate;

        /// <summary>Creates a code match</summary>
        /// <param name="opcode">The optional opcode</param>
        /// <param name="operand">The optional operand</param>
        /// <param name="name">The optional name</param>
        ///
        public CodeMatch(OpCode? opcode = null, object operand = null, string name = null)
        {
            if (opcode is OpCode opcodeValue) opcodes.Add(opcodeValue);
            if (operand != null) operands.Add(operand);
            this.name = name;
        }

        /// <summary>Creates a code match</summary>
        /// <param name="instruction">The CodeInstruction</param>
        /// <param name="name">An optional name</param>
        ///
        public CodeMatch(CodeInstruction instruction, string name = null) : this(
            instruction.opcode, instruction.operand, name)
        {
        }

        /// <summary>Creates a code match</summary>
        /// <param name="predicate">The predicate</param>
        /// <param name="name">An optional name</param>
        ///
        public CodeMatch(Func<CodeInstruction, bool> predicate, string name = null)
        {
            this.predicate = predicate;
            this.name = name;
        }

        internal bool Matches(List<CodeInstruction> codes, CodeInstruction instruction)
        {
            if (predicate != null) return predicate(instruction);

            if (opcodes.Count > 0 && opcodes.Contains(instruction.opcode) == false) return false;
            if (operands.Count > 0 && operands.Contains(instruction.operand) == false) return false;
            if (labels.Count > 0 && labels.Intersect(instruction.labels).Any() == false) return false;
            if (blocks.Count > 0 && blocks.Intersect(instruction.blocks).Any() == false) return false;

            if (jumpsFrom.Count > 0 && jumpsFrom.Select(index => codes[index].operand).OfType<Label>()
                                                .Intersect(instruction.labels).Any() == false) return false;

            if (jumpsTo.Count > 0)
            {
                var operand = instruction.operand;
                if (operand == null || operand.GetType() != typeof(Label)) return false;
                var label = (Label)operand;
                var indices = Enumerable.Range(0, codes.Count).Where(idx => codes[idx].labels.Contains(label));
                if (jumpsTo.Intersect(indices).Any() == false) return false;
            }

            return true;
        }

        /// <summary>Returns a string that represents the match</summary>
        /// <returns>A string representation</returns>
        ///
        public override string ToString()
        {
            var result = "[";
            if (name != null)
                result += $"{name}: ";
            if (opcodes.Count > 0)
                result += $"opcodes={opcodes.Join()} ";
            if (operands.Count > 0)
                result += $"operands={operands.Join()} ";
            if (labels.Count > 0)
                result += $"labels={labels.Join()} ";
            if (blocks.Count > 0)
                result += $"blocks={blocks.Join()} ";
            if (jumpsFrom.Count > 0)
                result += $"jumpsFrom={jumpsFrom.Join()} ";
            if (jumpsTo.Count > 0)
                result += $"jumpsTo={jumpsTo.Join()} ";
            if (predicate != null)
                result += "predicate=yes ";
            return $"{result.TrimEnd()}]";
        }
    }




    /// <summary>A CodeInstruction matcher</summary>
    public class HookBuilder : IEnumerable<CodeInstruction>
    {
        #region Fields
        private readonly ILGenerator generator;
        private readonly List<CodeInstruction> codes = new List<CodeInstruction>();
        private MethodBase OriginalMethod;
        protected List<LocalVariableInfo> LocalVariables;


        /// <summary>The current position</summary>
        /// <value>The index or -1 if out of bounds</value>
        ///
        public int Pos { get; private set; } = -1;

        private Dictionary<string, CodeInstruction> lastMatches = new Dictionary<string, CodeInstruction>();
        private string lastError;
        private bool lastUseEnd;
        private CodeMatch[] lastCodeMatches;

        private void FixStart()
        {
            Pos = Math.Max(0, Pos);
        }

        private void SetOutOfBounds(int direction)
        {
            Pos = direction > 0 ? Length : -1;
        }
         

        /// <summary>Gets instructions at the current position</summary>
        /// <value>The instruction</value>
        ///
        public CodeInstruction CurrentInstruction => GetAtIndex(Pos);
         
        public CodeInstruction PeekBehind(int amount = 1)
        {
            return Peek(-amount);
        }
        public CodeInstruction PeekAhead(int amount = 1)
        {
            return Peek(amount);
        }
        public CodeInstruction Peek(int amount)
        {
            return GetAtIndex(Pos + amount);
        } 
        private CodeInstruction GetAtIndex(int index)
        {
            if (index < 0 || index >= codes.Count)
            {
                return null;
            }
            return codes[index];
        }




        /// <summary>Gets the number of code instructions in this matcher</summary>
        /// <value>The count</value>
        ///
        public int Length => codes.Count;

        /// <summary>Checks whether the position of this CodeMatcher is within bounds</summary>
        /// <value>True if this CodeMatcher is valid</value>
        ///
        public bool IsValid => Pos >= 0 && Pos < Length;

        /// <summary>Checks whether the position of this CodeMatcher is outside its bounds</summary>
        /// <value>True if this CodeMatcher is invalid</value>
        ///
        public bool IsInvalid => Pos < 0 || Pos >= Length;

        /// <summary>Gets the remaining code instructions</summary>
        /// <value>The remaining count</value>
        ///
        public int Remaining => Length - Math.Max(0, Pos);

        /// <summary>Gets the opcode at the current position</summary>
        /// <value>The opcode</value>
        ///
        public ref OpCode Opcode => ref codes[Pos].opcode;

        /// <summary>Gets the operand at the current position</summary>
        /// <value>The operand</value>
        ///
        public ref object Operand => ref codes[Pos].operand;

        /// <summary>Gets the labels at the current position</summary>
        /// <value>The labels</value>
        ///
        public ref List<Label> Labels => ref codes[Pos].labels;

        /// <summary>Gets the exception blocks at the current position</summary>
        /// <value>The blocks</value>
        ///
        public ref List<ExceptionBlock> Blocks => ref codes[Pos].blocks;

        /// <summary>Creates an empty code matcher</summary>
        public HookBuilder()
        {
        }

        #endregion
         

        #region Init
        /// <summary>Creates a code matcher from an enumeration of instructions</summary>
        /// <param name="instructions">The instructions (transpiler argument)</param>
        /// <param name="generator">An optional IL generator</param>
        ///
        public HookBuilder(IEnumerable<CodeInstruction> instructions, MethodBase originalMethod, ILGenerator generator = null)
        {
            this.generator = generator;
            codes = instructions.Select(c => new CodeInstruction(c)).ToList();
            OriginalMethod = originalMethod;

            LocalVariables = originalMethod.GetMethodBody().LocalVariables.ToList();
        }

        /// <summary>Makes a clone of this instruction matcher</summary>
        /// <returns>A copy of this matcher</returns>
        ///
        public HookBuilder Clone()
        {
            return new HookBuilder(codes, OriginalMethod, generator)
            {
                Pos = Pos,
                lastMatches = lastMatches,
                lastError = lastError,
                lastUseEnd = lastUseEnd,
                lastCodeMatches = lastCodeMatches
            };
        }

        #endregion


        #region Helpers  
        // <summary>Advances the current position</summary>
        /// <param name="offset">The offset</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder Advance(int offset)
        {
            Pos += offset;
            if (IsValid == false) SetOutOfBounds(offset);
            return this;
        }


        /// <summary>Moves the current position to the start</summary>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder Start()
        {
            Pos = 0;
            return this;
        }


        /// <summary>Moves the current position to the end</summary>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder End()
        {
            Pos = Length - 1;
            return this;
        }


        /// <summary>Gets instructions at the current position with offset</summary>
        /// <param name="offset">The offset</param>
        /// <returns>The instruction</returns>
        ///
        public CodeInstruction InstructionAt(int offset)
        {
            return codes[Pos + offset];
        }


        /// <summary>Gets all instructions</summary>
        /// <returns>A list of instructions</returns>
        ///
        public List<CodeInstruction> Instructions()
        {
            return codes;
        }


        /// <summary>Gets some instructions counting from current position</summary>
        /// <param name="count">Number of instructions</param>
        /// <returns>A list of instructions</returns>
        ///
        public List<CodeInstruction> Instructions(int count)
        {
            return codes.GetRange(Pos, count).Select(c => new CodeInstruction(c)).ToList();
        }


        /// <summary>Gets all instructions within a range</summary>
        /// <param name="start">The start index</param>
        /// <param name="end">The end index</param>
        /// <returns>A list of instructions</returns>
        ///
        public List<CodeInstruction> InstructionsInRange(int start, int end)
        {
            var instructions = codes;
            if (start > end)
            {
                var tmp = start;
                start = end;
                end = tmp;
            }

            instructions = instructions.GetRange(start, end - start + 1);
            return instructions.Select(c => new CodeInstruction(c)).ToList();
        }


        /// <summary>Gets all instructions within a range (relative to current position)</summary>
        /// <param name="startOffset">The start offset</param>
        /// <param name="endOffset">The end offset</param>
        /// <returns>A list of instructions</returns>
        ///
        public List<CodeInstruction> InstructionsWithOffsets(int startOffset, int endOffset)
        {
            return InstructionsInRange(Pos + startOffset, Pos + endOffset);
        }


        /// <summary>Gets a list of all distinct labels</summary>
        /// <param name="instructions">The instructions (transpiler argument)</param>
        /// <returns>A list of Labels</returns>
        ///
        public List<Label> DistinctLabels(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.SelectMany(instruction => instruction.labels).Distinct().ToList();
        }


        /// <summary>Reports a failure</summary>
        /// <param name="method">The method involved</param>
        /// <param name="logger">The logger</param>
        /// <returns>True if current position is invalid and error was logged</returns>
        ///
        public bool ReportFailure(MethodBase method, Action<string> logger)
        {
            if (IsValid) return false;
            var err = lastError ?? "Unexpected code";
            logger($"{err} in {method}");
            return true;
        }


        /// <summary>Throw an InvalidOperationException if current state is invalid (position out of bounds / last match failed)</summary>
        /// <param name="explanation">Explanation of where/why the exception was thrown that will be added to the exception message</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder ThrowIfInvalid(string explanation)
        {
            if (explanation == null) throw new ArgumentNullException(nameof(explanation));
            if (IsInvalid) throw new InvalidOperationException(explanation + " - Current state is invalid");
            return this;
        }


        /// <summary>Throw an InvalidOperationException if current state is invalid (position out of bounds / last match failed),
        /// or if the matches do not match at current position</summary>
        /// <param name="explanation">Explanation of where/why the exception was thrown that will be added to the exception message</param>
        /// <param name="matches">Some code matches</param>
        /// <returns>The same code matcher</returns>
        /// 
        public HookBuilder ThrowIfNotMatch(string explanation, params CodeMatch[] matches)
        {
            ThrowIfInvalid(explanation);
            if (!MatchSequence(Pos, matches)) throw new InvalidOperationException(explanation + " - Match failed");
            return this;
        }

        private void ThrowIfNotMatch(string explanation, int direction, CodeMatch[] matches)
        {
            ThrowIfInvalid(explanation);

            var tempPos = Pos;
            try
            {
                if (Match(matches, direction, false).IsInvalid)
                    throw new InvalidOperationException(explanation + " - Match failed");
            }
            finally
            {
                Pos = tempPos;
            }
        }


        /// <summary>Throw an InvalidOperationException if current state is invalid (position out of bounds / last match failed),
        /// or if the matches do not match at any point between current position and the end</summary>
        /// <param name="explanation">Explanation of where/why the exception was thrown that will be added to the exception message</param>
        /// <param name="matches">Some code matches</param>
        /// <returns>The same code matcher</returns>
        /// 
        public HookBuilder ThrowIfNotMatchForward(string explanation, params CodeMatch[] matches)
        {
            ThrowIfNotMatch(explanation, 1, matches);
            return this;
        }


        /// <summary>Throw an InvalidOperationException if current state is invalid (position out of bounds / last match failed),
        /// or if the matches do not match at any point between current position and the start</summary>
        /// <param name="explanation">Explanation of where/why the exception was thrown that will be added to the exception message</param>
        /// <param name="matches">Some code matches</param>
        /// <returns>The same code matcher</returns>
        /// 
        public HookBuilder ThrowIfNotMatchBack(string explanation, params CodeMatch[] matches)
        {
            ThrowIfNotMatch(explanation, -1, matches);
            return this;
        }


        /// <summary>Throw an InvalidOperationException if current state is invalid (position out of bounds / last match failed),
        /// or if the check function returns false</summary>
        /// <param name="explanation">Explanation of where/why the exception was thrown that will be added to the exception message</param>
        /// <param name="stateCheckFunc">Function that checks validity of current state. If it returns false, an exception is thrown</param>
        /// <returns>The same code matcher</returns>
        /// 
        public HookBuilder ThrowIfFalse(string explanation, Func<HookBuilder, bool> stateCheckFunc)
        {
            if (stateCheckFunc == null) throw new ArgumentNullException(nameof(stateCheckFunc));

            ThrowIfInvalid(explanation);

            if (!stateCheckFunc(this)) throw new InvalidOperationException(explanation + " - Check function returned false");

            return this;
        }
        #endregion


        #region Code Modifiers

        #region Sets
        /// <summary>Sets an instruction at current position</summary>
        /// <param name="instruction">The instruction to set</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SetInstruction(CodeInstruction instruction)
        {
            codes[Pos] = instruction;
            return this;
        }


        /// <summary>Sets instruction at current position and advances</summary>
        /// <param name="instruction">The instruction</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SetInstructionAndAdvance(CodeInstruction instruction)
        {
            SetInstruction(instruction);
            Pos++;
            return this;
        }

        /// <summary>Sets opcode and operand at current position</summary>
        /// <param name="opcode">The opcode</param>
        /// <param name="operand">The operand</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder Set(OpCode opcode, object operand)
        {
            Opcode = opcode;
            Operand = operand;
            return this;
        }


        /// <summary>Sets opcode and operand at current position and advances</summary>
        /// <param name="opcode">The opcode</param>
        /// <param name="operand">The operand</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SetAndAdvance(OpCode opcode, object operand)
        {
            Set(opcode, operand);
            Pos++;
            return this;
        }


        /// <summary>Sets opcode at current position and advances</summary>
        /// <param name="opcode">The opcode</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SetOpcodeAndAdvance(OpCode opcode)
        {
            Opcode = opcode;
            Pos++;
            return this;
        }



        /// <summary>Sets operand at current position and advances</summary>
        /// <param name="operand">The operand</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SetOperandAndAdvance(object operand)
        {
            Operand = operand;
            Pos++;
            return this;
        }
        #endregion


        #region Labels
        /// <summary>Creates a label at current position</summary>
        /// <param name="label">[out] The label</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder CreateLabel(out Label label)
        {
            label = generator.DefineLabel();
            Labels.Add(label);
            return this;
        }

        /// <summary>Creates a label at a position</summary>
        /// <param name="position">The position</param>
        /// <param name="label">[out] The new label</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder CreateLabelAt(int position, out Label label)
        {
            label = generator.DefineLabel();
            AddLabelsAt(position, new[] { label });
            return this;
        }

        /// <summary>Adds an enumeration of labels to current position</summary>
        /// <param name="labels">The labels</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder AddLabels(IEnumerable<Label> labels)
        {
            Labels.AddRange(labels);
            return this;
        }

        /// <summary>Adds an enumeration of labels at a position</summary>
        /// <param name="position">The position</param>
        /// <param name="labels">The labels</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder AddLabelsAt(int position, IEnumerable<Label> labels)
        {
            codes[position].labels.AddRange(labels);
            return this;
        }

        /// <summary>Sets jump to</summary>
        /// <param name="opcode">Branch instruction</param>
        /// <param name="destination">Destination for the jump</param>
        /// <param name="label">[out] The created label</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SetJumpTo(OpCode opcode, int destination, out Label label)
        {
            CreateLabelAt(destination, out label);
            Set(opcode, label);
            return this;
        }
        #endregion


        #region Inserts
        /// <summary>Inserts some instructions</summary>
        /// <param name="instructions">The instructions</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder Insert(params CodeInstruction[] instructions)
        {
            codes.InsertRange(Pos, instructions);
            return this;
        }

        /// <summary>Inserts an enumeration of instructions</summary>
        /// <param name="instructions">The instructions</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder Insert(IEnumerable<CodeInstruction> instructions)
        {
            codes.InsertRange(Pos, instructions);
            return this;
        }

        /// <summary>Inserts a branch</summary>
        /// <param name="opcode">The branch opcode</param>
        /// <param name="destination">Branch destination</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder InsertBranch(OpCode opcode, int destination)
        {
            CreateLabelAt(destination, out var label);
            codes.Insert(Pos, new CodeInstruction(opcode, label));
            return this;
        }

        /// <summary>Inserts some instructions and advances the position</summary>
        /// <param name="instructions">The instructions</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder InsertAndAdvance(params CodeInstruction[] instructions)
        {
            foreach (var instruction in instructions)
            {
                Insert(instruction);
                Pos++;
            }

            return this;
        }

        /// <summary>Inserts an enumeration of instructions and advances the position</summary>
        /// <param name="instructions">The instructions</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder InsertAndAdvance(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
                InsertAndAdvance(instruction);
            return this;
        }

        public HookBuilder InsertAndAdvance(OpCode code, object operand = null)
        {
            return InsertAndAdvance(new CodeInstruction(code, operand));
        }

        /// <summary>Inserts a branch and advances the position</summary>
        /// <param name="opcode">The branch opcode</param>
        /// <param name="destination">Branch destination</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder InsertBranchAndAdvance(OpCode opcode, int destination)
        {
            InsertBranch(opcode, destination);
            Pos++;
            return this;
        }
        #endregion


        #region Removes
        /// <summary>Removes current instruction</summary>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder RemoveInstruction()
        {
            codes.RemoveAt(Pos);
            return this;
        }

        /// <summary>Removes some instruction fro current position by count</summary>
        /// <param name="count">Number of instructions</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder RemoveInstructions(int count)
        {
            codes.RemoveRange(Pos, count);
            return this;
        }


        /// <summary>Removes the instructions in a range</summary>
        /// <param name="start">The start</param>
        /// <param name="end">The end</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder RemoveInstructionsInRange(int start, int end)
        {
            if (start > end)
            {
                var tmp = start;
                start = end;
                end = tmp;
            }

            codes.RemoveRange(start, end - start + 1);
            return this;
        }


        /// <summary>Removes the instructions in a offset range</summary>
        /// <param name="startOffset">The start offset</param>
        /// <param name="endOffset">The end offset</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder RemoveInstructionsWithOffsets(int startOffset, int endOffset)
        {
            RemoveInstructionsInRange(Pos + startOffset, Pos + endOffset);
            return this;
        } 
        #endregion

        #endregion


        #region Enumerable   
        IEnumerator<CodeInstruction> IEnumerable<CodeInstruction>.GetEnumerator()
        {
            return codes.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return codes.GetEnumerator();
        }
        #endregion

          
        #region Hook Insertion
        public enum HookType
        {
            Continue,
            Return,
            ModifyRefArg
        } 

        public static MethodInfo? GetInvokeHandlerMethod(Type target, string name, List<Type> parameters, Type? ReturnType = null)
        {
            var methods = target.GetMethods();

            foreach(var method in methods.Where(m => m.Name == name))
            {
                var methodParamTypes = method.GetParameters();

                if (methodParamTypes.Length == parameters.Count + 1)
                {
                    if (ReturnType != null)
                        parameters.Add(ReturnType);

                    return method.MakeGenericMethod(parameters.ToArray());
                }
            }

            return null;
        }

        public static MethodInfo? GetHasValue(Type nullable, Type target, string name)
        {
            var methods = target.GetMethods();

            foreach (var method in methods)
            {
                Debug.Log(method.ToString());

                if (method.Name != name)
                    continue;

                var methodParamTypes = method.GetParameters();

                return method.MakeGenericMethod(target);
            }

            return null;
        }

        /*
        public HookBuilder InsertHook(string name, params Arg[] arguments)
        { 
            LoadHookStringAndFixLabel(name);

            // list of types to target the generic hook invoker
            List<Type> TargetTypes = new List<Type>();

            LoadArguments(TargetTypes, arguments);

            // todo: add option for "should return" and other types of hooks

            Debug.Log("Target Types:");
            foreach(var t in TargetTypes)
            {
                Debug.Log(t.FullName);
            }

            var targetInvokeHandler = GetInvokeHandlerMethod(typeof(Radium), nameof(Radium.InvokeProcedure), TargetTypes);

            if (targetInvokeHandler == null)
                throw new Exception("Target Invoke Handler Was Null, TargetTypes Probably Fucked");

            InsertAndAdvance(OpCodes.Call, targetInvokeHandler);
            return this;
        }

        public HookBuilder InsertReturnHook(string name, params Arg[] arguments)
        { 
            LoadHookStringAndFixLabel(name);

            // list of types to target the generic hook invoker
            List<Type> TargetTypes = new List<Type>();
            LoadArguments(TargetTypes, arguments);
             
            Type ReturnType = null; 
            if(OriginalMethod is MethodInfo method)
                ReturnType = method.ReturnType;

            if (OriginalMethod is ConstructorInfo constructor)
                ReturnType = constructor.DeclaringType;

            if (ReturnType == null)
                throw new Exception("Could not find return type of method");
             

            if (ReturnType == typeof(void))
            {
                // InvokeFunction returns bool?
                var targetInvokeHandler = GetInvokeHandlerMethod(typeof(Radium), nameof(Radium.InvokeFunction), TargetTypes, typeof(bool?));
                if (targetInvokeHandler == null)
                    throw new Exception("Could not find TargetInvokeHandler in return void hook");

                InsertAndAdvance(OpCodes.Call, targetInvokeHandler);
                 
                // make a new local var, stloc, and then load the address for HasValue to work
                var boolLocal = generator.DeclareLocal(typeof(bool?));
                InsertAndAdvance(OpCodes.Stloc, boolLocal);
                InsertAndAdvance(OpCodes.Ldloca_S, boolLocal); 
                 
                InsertAndAdvance(OpCodes.Call, AccessTools.Method(typeof(bool?), "get_HasValue"));
                 
                //set label on current method to continue if HasValue == false
                var postHookLabel = generator.DefineLabel();
                CurrentInstruction.labels.Add(postHookLabel);

                // continue if null, otherwise return void
                InsertAndAdvance(OpCodes.Brtrue_S, postHookLabel); 
                InsertAndAdvance(OpCodes.Ret);

                return this;
            }

            if (ReturnType.IsValueType)
            {
                
            }
            else
            {

            }

            return this; 
        }

        public HookBuilder InsertReturnBoxedValueIfNotNullHook(string name, params Arg[] arguments)
        {
            LoadHookStringAndFixLabel(name);

            // list of types to target the generic hook invoker
            List<Type> TargetTypes = new List<Type>(arguments.Length);

            LoadArguments(TargetTypes, arguments);

            
            // todo: add option for "should return" and other types of hooks

            var targetInvokeHandler = AccessTools.Method(typeof(Radium), nameof(Radium.InvokeProcedure), null, TargetTypes.ToArray());
            InsertAndAdvance(OpCodes.Call, targetInvokeHandler);
            InsertAndAdvance(OpCodes.Pop); 
            return this;
        }
        */
         
        public HookBuilder LoadArguments(List<Type>? TargetTypes = null, params Arg[] arguments)
        {
            // load arguments dynamically
            int counter = 0;
            foreach (var argPoly in arguments)
            {
                switch (argPoly.SearchType)
                {
                    case ArgType.Call:
                        {                           
                            throw new NotSupportedException();

                            // var arg = argPoly as ArgCall;
                            //
                            // if (counter > 0)
                            //     throw new Exception("ArgCall Must Be The First Element in Argument Array");
                            //
                            // InsertDirectCall(arg.TargetMethod, arg.Parameters.Values);
                            //
                            // TargetTypes?.Add(arg.Type);
                            // break;
                        }
                    case ArgType.Value:
                        {
                            var arg = argPoly as ArgValue;

                            switch(arg.Value)
                            {
                                case string:
                                    {
                                        InsertAndAdvance(OpCodes.Ldstr, (string)arg.Value);
                                        TargetTypes?.Add(typeof(string));
                                        break;
                                    }
                                case int:
                                    {
                                        InsertAndAdvance(OpCodes.Ldc_I4, (int)arg.Value);
                                        TargetTypes?.Add(typeof(int));
                                        break;
                                    }
                                case float:
                                    {
                                        InsertAndAdvance(OpCodes.Ldc_R4, (float)arg.Value);
                                        TargetTypes?.Add(typeof(float));
                                        break;
                                    }
                                case null: 
                                    {
                                        throw new Exception("Type not supported by ArgValue");
                                    }
                            } 

                            TargetTypes?.Add(OriginalMethod.DeclaringType);
                            break;
                        }
                    case ArgType.This:
                        {
                            if (OriginalMethod.IsStatic || (OriginalMethod.DeclaringType.IsAbstract && OriginalMethod.DeclaringType.IsSealed))
                                // static classes are abstract and sealed at IL level
                                throw new Exception("Attempting to load 'this' on static method or class");

                            InsertAndAdvance(OpCodes.Ldarg_0);
                            TargetTypes?.Add(OriginalMethod.DeclaringType);
                            break;
                        }
                    case ArgType.Parameter:
                        {
                            var arg = argPoly as ArgParameter;

                            // check if parameter type matches, facepunch could change method params and break the index
                            var parameters = OriginalMethod.GetParameters();
                            if (arg.Index > parameters.Length - 1)
                                throw new ArgumentException("Parameter index does not exist");

                            var param = parameters[arg.Index];
                            if (param.ParameterType != arg.Type)
                                throw new ArgumentException("Parameter type does not match type found on method at given index");

                            LoadParameter(arg.Index);

                            TargetTypes?.Add(arg.Type);
                            break;
                        }
                    case ArgType.Field:
                        {
                            var arg = argPoly as ArgField;

                            InsertAndAdvance(OpCodes.Ldfld, arg.Field);

                            TargetTypes?.Add(arg.Field.FieldType);
                            break;
                        }
                    case ArgType.Local:
                        {
                            // todo: maybe add a way to differentiate between two locals of the same type?

                            var arg = argPoly as ArgLocal;

                            SearchStoreLocal(-1, arg.Type, out var localVariableInfo);

                            if (localVariableInfo == null)
                                throw new ArgumentException("Could not find stloc for given type");

                            LoadLocal(localVariableInfo.LocalIndex);

                            TargetTypes?.Add(localVariableInfo.LocalType);
                            break;
                        }
                }
                counter++;
            }

            return this;
        }
         

        #region ILCode Helpers

        public void ReturnIfNotNull()
        {
            InsertAndAdvance(OpCodes.Ldnull);
            var postHookLabel = generator.DefineLabel();
            PeekAhead().labels.Add(postHookLabel);
            InsertAndAdvance(OpCodes.Beq_S, postHookLabel);
            InsertAndAdvance(OpCodes.Ret);
        }

        protected void LoadThis()
        {
            InsertAndAdvance(OpCodes.Ldarg_0);
        }

        protected void LoadParameter(int index)
        {
            //Add 1 to argument index when instance method because arg0 = this
            if (OriginalMethod.IsStatic == false)
            {
                index++;
            }

            switch (index)
            {
                case 0:
                    {
                        InsertAndAdvance(OpCodes.Ldarg_0);
                        return;
                    }
                case 1:
                    {
                        InsertAndAdvance(OpCodes.Ldarg_1);
                        return;
                    }
                case 2:
                    {
                        InsertAndAdvance(OpCodes.Ldarg_2);
                        return;
                    }
                case 3:
                    {
                        InsertAndAdvance(OpCodes.Ldarg_3);
                        return;
                    }
                default:
                    {
                        InsertAndAdvance(OpCodes.Ldarg_S, index);
                        return;
                    }
            }
        }
         
        protected void LoadLocal(int index)
        {
            switch (index)
            {
                case 0:
                    {
                        InsertAndAdvance(OpCodes.Ldloc_0);
                        return;
                    }
                case 1:
                    {
                        InsertAndAdvance(OpCodes.Ldloc_1);
                        return;
                    }
                case 2:
                    {
                        InsertAndAdvance(OpCodes.Ldloc_2);
                        return;
                    }
                case 3:
                    {
                        InsertAndAdvance(OpCodes.Ldloc_3);
                        return;
                    }
                default:
                    {
                        InsertAndAdvance(OpCodes.Ldloc_S, index);
                        return;
                    }
            }
        } 

        protected void LoadBool(bool state)
        {
            if (state)
            {
                InsertAndAdvance(OpCodes.Ldc_I4_1);
            }
            else
            {
                InsertAndAdvance(OpCodes.Ldc_I4_0);
            }
        }

        protected void LoadHookStringAndFixLabel(string text)
        {
            var instruction = new CodeInstruction(OpCodes.Ldstr, text);

            FixLabels(instruction);

            InsertAndAdvance(instruction);
        }

        protected void FixLabels(CodeInstruction newtarget)
        {
            if (CurrentInstruction.labels != null && CurrentInstruction.labels.Count > 0)
            {
                while (CurrentInstruction.labels.Count > 0)
                {
                    var label = CurrentInstruction.labels[0];
                    newtarget.labels.Add(label);
                    CurrentInstruction.labels.RemoveAt(0);
                }
            }
        }

        public void SearchLoadLocal(int direction, Type localType, out LocalVariableInfo local)
        {
            // todo: fix this so it supports methods with multiple locals of the same type
            local = null;
            foreach (var loc in LocalVariables)
            {
                Debug.Log(loc.LocalIndex + " : " + loc.LocalType);

                if (loc.LocalType == localType)
                    local = loc;
            }
            return;

            CodeInstruction? searchResult = SearchStateless(direction, instruction => { return instruction.CheckLoadLocal(localType, LocalVariables); });

            Console.WriteLine("Search Local " + searchResult);

            if (searchResult == null)
            {
                local = null;
            }
            else
            {
                local = LocalVariables[GetLoadLocalIndex(searchResult)];
            }

            return;
        }

        public void SearchStoreLocal(int direction, Type localType, out LocalVariableInfo local)
        {
            Debug.Log("searching for: " + localType);

            local = null;
            foreach (var loc in LocalVariables)
            {
                Debug.Log(loc.LocalIndex + " : " + loc.LocalType);

                if (loc.LocalType.FullName == localType.FullName)
                {
                    Debug.Log("Found Local");

                    local = loc;

                    return;
                }
            }
            return;



            CodeInstruction? searchResult = SearchStateless(direction, instruction => { return instruction.CheckStoreLocal(localType, LocalVariables); });

            if (searchResult == null)
                throw new Exception("SearchStoreLocal returned null");

            local = LocalVariables[GetStoreLocalIndex(searchResult)];
            if (local == null)
                throw new Exception("SearchStoreLocal was null");

            return;
        }

        private CodeInstruction? SearchStateless(int direction, Func<CodeInstruction, bool> search)
        {
            for (int index = Pos; index < codes.Count && index >= 0; index += direction)
            {
                if (search(codes[index]))
                {
                    return codes[index];
                }
            }

            return null;
        }
         
        public int GetLoadLocalIndex(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldloc_0)
            {
                return 0;
            }
            else if (instruction.opcode == OpCodes.Ldloc_1)
            {
                return 1;
            }
            else if (instruction.opcode == OpCodes.Ldloc_2)
            {
                return 2;
            }
            else if (instruction.opcode == OpCodes.Ldloc_3)
            {
                return 3;
            }
            else if (instruction.opcode == OpCodes.Ldloc_S || instruction.opcode == OpCodes.Ldloc)
            {
                return (instruction.operand as LocalVariableInfo).LocalIndex;
            }
            else
            {
                return -1;
            }
        }

        public int GetStoreLocalIndex(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Stloc_0)
            {
                return 0;
            }
            else if (instruction.opcode == OpCodes.Stloc_1)
            {
                return 1;
            }
            else if (instruction.opcode == OpCodes.Stloc_2)
            {
                return 2;
            }
            else if (instruction.opcode == OpCodes.Stloc_3)
            {
                return 3;
            }
            else if (instruction.opcode == OpCodes.Stloc_S || instruction.opcode == OpCodes.Stloc)
            {
                return (instruction.operand as LocalVariableInfo).LocalIndex;
            }
            else
            {
                return -1;
            }
        }

        #endregion

        #endregion


        #region Delegate Insertion
        /*
        private static readonly Dictionary<int, Delegate> DelegateCache = new Dictionary<int, Delegate>();
        private static int delegateCounter;

        public static CodeInstruction EmitDelegate<T>(T action) where T : Delegate
        {
            if (action.Method.IsStatic && action.Target == null) return new CodeInstruction(OpCodes.Call, action.Method);

            var paramTypes = action.Method.GetParameters().Select(x => x.ParameterType).ToArray();

            var dynamicMethod = new DynamicMethodDefinition(action.Method.Name,
                action.Method.ReturnType,
                paramTypes);

            var il = dynamicMethod.GetILGenerator();

            var targetType = action.Target.GetType();

            var preserveContext = action.Target != null && targetType.GetFields().Any(x => !x.IsStatic);

            if (preserveContext)
            {
                var currentDelegateCounter = delegateCounter++;

                DelegateCache[currentDelegateCounter] = action;

                var cacheField = AccessTools.Field(typeof(Transpilers), nameof(DelegateCache));

                var getMethod = AccessTools.Method(typeof(Dictionary<int, Delegate>), "get_Item");

                il.Emit(OpCodes.Ldsfld, cacheField);
                il.Emit(OpCodes.Ldc_I4, currentDelegateCounter);
                il.Emit(OpCodes.Callvirt, getMethod);
            }
            else
            {
                if (action.Target == null)
                    il.Emit(OpCodes.Ldnull);
                else
                    il.Emit(OpCodes.Newobj,
                        AccessTools.FirstConstructor(targetType, x => x.GetParameters().Length == 0 && !x.IsStatic));

                il.Emit(OpCodes.Ldftn, action.Method);
                il.Emit(OpCodes.Newobj, AccessTools.Constructor(typeof(T), new[] { typeof(object), typeof(IntPtr) }));
            }

            for (var i = 0; i < paramTypes.Length; i++)
                il.Emit(OpCodes.Ldarg_S, (short)i);

            il.Emit(OpCodes.Callvirt, AccessTools.Method(typeof(T), "Invoke"));
            il.Emit(OpCodes.Ret);

            return new CodeInstruction(OpCodes.Call, dynamicMethod.Generate());
        }
        */
        #endregion


        #region Call Insertion
        public HookBuilder InsertDirectCall(MethodInfo targetMethod, params Arg[] arguments)
        {
            // list of types to target the generic hook invoker
            List<Type> TargetTypes = new List<Type>(); 
            LoadArguments(TargetTypes, arguments);
 
            if (targetMethod == null)
                throw new Exception("Target Invoke Handler For InsertDirectCall Was Null");

            InsertAndAdvance(OpCodes.Call, targetMethod);

            FixLabels(PeekBehind(1 + arguments.Length)); 
            return this;
        }
        #endregion


        #region Method Searching
        public bool IsMethodCall(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Calli;
        }

        public int GetStackModification(CodeInstruction instruction)
        {
            //Console.WriteLine( $"Stack: {instruction} Pop: {instruction.opcode.StackBehaviourPop} Push: {instruction.opcode.StackBehaviourPush}" );

            return GetStackBehaviour(instruction.opcode.StackBehaviourPush) + GetStackBehaviour(instruction.opcode.StackBehaviourPop);
        }

        private int GetStackBehaviour(StackBehaviour behaviour)
        {
            var name = behaviour.ToString();

            var split = behaviour.ToString().Split('_');

            switch (behaviour)
            {
                case StackBehaviour.Pop0:
                case StackBehaviour.Push0:
                    {
                        return 0;
                    }
            }

            if (name.StartsWith("Push"))
            {
                return split.Length;
            }
            else if (name.StartsWith("Pop"))
            {
                return -split.Length;
            }
            else if (behaviour == StackBehaviour.Varpop)
            {
                return -1;
            }
            else if (behaviour == StackBehaviour.Varpush)
            {
                return 1;
            }

            return 0;
        }


        public HookBuilder SearchForMethod(Type DeclaringType, string MethodName, Type[] MethodParameters = null, Type[] Generics = null)
        { 
            Search(
               (instruction) =>
               {
                   if (!IsMethodCall(instruction))
                       return false;

                   var method = instruction.operand as MethodInfo;
                   if (method == null)
                       return false;

                   if (method.Name != MethodName)
                       return false;

                   Debug.Log("SearchForMethod: " + instruction.ToString());

                   // check declaring type matches
                   if (DeclaringType != null && method.DeclaringType != DeclaringType)
                       return false;

                   if (MethodParameters != null)
                   {
                       var parameters = method.GetParameters();
                       if (parameters != null && parameters.Length > 0)
                       {
                           for (int i = 0; i < parameters.Length; i++)
                           {
                               // method has too many parameters
                               if (i > MethodParameters.Length - 1)
                                   return false;

                               if (parameters[i].ParameterType != MethodParameters[i])
                                   return false;
                           }
                       }
                   } 

                   if (Generics != null)
                   {
                       var genparams = method.GetGenericArguments();
                       if (genparams != null && genparams.Length > 0)
                       {
                           for (int i = 0; i < genparams.Length; i++)
                           {
                               // method has too many parameters
                               if (i > Generics.Length - 1)
                                   return false;

                               if (genparams[i] != Generics[i])
                                   return false;
                           }
                       }
                   } 

                   return true;
               },
               1
            );

            return this;
        }
         
           
        public HookBuilder MoveBeforeMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            SearchForMethod(type, name, parameters, generics); 
              
            if (IsInvalid)
                throw new Exception("Could not find method definition"); 

            if (!(CurrentInstruction.operand is MethodInfo))
                throw new Exception("MethodInfo is all fucked"); 


            MethodInfo method = (MethodInfo)CurrentInstruction.operand;
             
            int stackSizeLeft = -method.GetParameters().Length;

            if (method.IsStatic == false)
                stackSizeLeft--; 

            int i = 1; 
            var instruction = PeekBehind(i);

            while (instruction != null && stackSizeLeft != 0)
            {
                int stackchange = GetStackModification(instruction);

                Console.WriteLine($"MoveBeforeMethod() {instruction} {stackSizeLeft} Change: {stackchange}");
                stackSizeLeft += stackchange;

                instruction = PeekBehind(++i);
            }

            if (stackSizeLeft == 0)
                Pos -= i - 1;  

            return this;
        }
         

        public HookBuilder MoveAfterMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            SearchForMethod(type, name, parameters, generics);
            
            Pos++;

            if (IsInvalid) 
                throw new Exception("MoveAfterMethod produced invalid index");

            if (CurrentInstruction.opcode == OpCodes.Pop)
            {
                Pos++; 
                if (IsInvalid)
                    throw new Exception("MoveAfterMethod produced invalid index after pop"); 
            } 

            return this;
        }

        #endregion

           
        #region Search & Match
        /// <summary>Searches forward with a predicate and advances position</summary>
        /// <param name="predicate">The predicate</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SearchForward(Func<CodeInstruction, bool> predicate)
        {
            return Search(predicate, 1);
        }

        /// <summary>Searches backwards with a predicate and reverses position</summary>
        /// <param name="predicate">The predicate</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder SearchBack(Func<CodeInstruction, bool> predicate)
        {
            return Search(predicate, -1);
        }

        private HookBuilder Search(Func<CodeInstruction, bool> predicate, int direction)
        {
            FixStart();
            while (IsValid && predicate(CurrentInstruction) == false)
                Pos += direction;
            lastError = IsInvalid ? $"Cannot find {predicate}" : null;
            return this;
        }

        /// <summary>Matches forward and advances position</summary>
        /// <param name="useEnd">True to set position to end of match, false to set it to the beginning of the match</param>
        /// <param name="matches">Some code matches</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder MatchForward(bool useEnd, params CodeMatch[] matches)
        {
            return Match(matches, 1, useEnd);
        }

        /// <summary>Matches backwards and reverses position</summary>
        /// <param name="useEnd">True to set position to end of match, false to set it to the beginning of the match</param>
        /// <param name="matches">Some code matches</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder MatchBack(bool useEnd, params CodeMatch[] matches)
        {
            return Match(matches, -1, useEnd);
        }

        private HookBuilder Match(CodeMatch[] matches, int direction, bool useEnd)
        {
            FixStart();
            while (IsValid)
            {
                lastUseEnd = useEnd;
                lastCodeMatches = matches;
                if (MatchSequence(Pos, matches))
                {
                    if (useEnd) 
                        Pos += matches.Count() - 1;
                    break;
                }

                Pos += direction;
            }

            lastError = IsInvalid ? $"Cannot find {matches.Join()}" : null;
            return this;
        }



        /// <summary>Repeats a match action until boundaries are met</summary>
        /// <param name="matchAction">The match action</param>
        /// <param name="notFoundAction">An optional action that is executed when no match is found</param>
        /// <returns>The same code matcher</returns>
        ///
        public HookBuilder Repeat(Action<HookBuilder> matchAction, Action<string> notFoundAction = null)
        {
            var count = 0;
            if (lastCodeMatches == null)
                throw new InvalidOperationException("No previous Match operation - cannot repeat");

            while (IsValid)
            {
                matchAction(this);
                MatchForward(lastUseEnd, lastCodeMatches);
                count++;
            }

            lastCodeMatches = null;

            if (count == 0 && notFoundAction != null)
                notFoundAction(lastError);

            return this;
        }

        /// <summary>Gets a match by its name</summary>
        /// <param name="name">The match name</param>
        /// <returns>An instruction</returns>
        ///
        public CodeInstruction NamedMatch(string name)
        {
            return lastMatches[name];
        }

        private bool MatchSequence(int start, CodeMatch[] matches)
        {
            if (start < 0) return false;
            lastMatches = new Dictionary<string, CodeInstruction>();

            foreach (var match in matches)
            {
                if (start >= Length || match.Matches(codes, codes[start]) == false)
                    return false;

                if (match.name != null)
                    lastMatches.Add(match.name, codes[start]);

                start++;
            }

            return true;
        } 
        #endregion

    }
}
