﻿using System;
using System.Collections.Generic;
using System.Linq;
using CodeGeneration;

namespace ABT {
    public enum StorageClass {
        AUTO,
        STATIC,
        EXTERN,
        TYPEDEF
    }

    public sealed class Decln : ExternDecln {
        public Decln(String name, StorageClass scs, ExprType type, Option<Initr> initr) {
            this.name = name;
            this.scs = scs;
            this.type = type;
            this.initr = initr;
        }

        public override String ToString() {
            String str = "[" + this.scs + "] ";
            str += this.name;
            str += " : " + this.type;
            return str;
        }

        // * function;
        // * extern function;
        // * static function;
        // * obj;
        // * obj = Init;
        // * static obj;
        // * static obj = Init;
        // * extern obj;
        // * extern obj = Init;
        public void CGenDecln(Env env, CGenState state) {

            if (env.IsGlobal()) {

                if (this.initr.IsSome) {
                    Initr initr = this.initr.Value;
                    switch (this.scs) {
                        case StorageClass.AUTO:
                            state.GLOBL(this.name);
                            break;

                        case StorageClass.EXTERN:
                            throw new InvalidProgramException();

                        case StorageClass.STATIC:
                            break;

                        case StorageClass.TYPEDEF:
                            // Ignore.
                            return;

                        default:
                            throw new InvalidProgramException();
                    }

                    state.DATA();

                    state.ALIGN(ExprType.ALIGN_LONG);

                    state.CGenLabel(this.name);

                    Int32 last = 0;
                    initr.Iterate(this.type, (Int32 offset, Expr expr) => {
                        if (offset > last) {
                            state.ZERO(offset - last);
                        }

                        if (!expr.IsConstExpr) {
                            throw new InvalidOperationException("Cannot initialize with non-const expression.");
                        }

                        switch (expr.Type.Kind) {
                            // TODO: without const char/short, how do I initialize?
                            case ExprTypeKind.CHAR:
                            case ExprTypeKind.UCHAR:
                            case ExprTypeKind.SHORT:
                            case ExprTypeKind.USHORT:
                                throw new NotImplementedException();
                            case ExprTypeKind.LONG:
                                state.LONG(((ConstLong)expr).Value);
                                break;

                            case ExprTypeKind.ULONG:
                                state.LONG((Int32)((ConstULong)expr).Value);
                                break;

                            case ExprTypeKind.POINTER:
                                state.LONG((Int32)((ConstPtr)expr).Value);
                                break;

                            case ExprTypeKind.FLOAT:
                                byte[] float_bytes = BitConverter.GetBytes(((ConstFloat)expr).Value);
                                Int32 intval = BitConverter.ToInt32(float_bytes, 0);
                                state.LONG(intval);
                                break;

                            case ExprTypeKind.DOUBLE:
                                byte[] double_bytes = BitConverter.GetBytes(((ConstDouble)expr).Value);
                                Int32 first_int = BitConverter.ToInt32(double_bytes, 0);
                                Int32 second_int = BitConverter.ToInt32(double_bytes, 4);
                                state.LONG(first_int);
                                state.LONG(second_int);
                                break;

                            default:
                                throw new InvalidProgramException();
                        }

                        last = offset + expr.Type.SizeOf;
                    });

                } else {

                    // Global without initialization.

                    switch (this.scs) {
                        case StorageClass.AUTO:
                            // .comm name,size,align
                            break;

                        case StorageClass.EXTERN:
                            break;

                        case StorageClass.STATIC:
                            // .local name
                            // .comm name,size,align
                            state.LOCAL(this.name);
                            break;

                        case StorageClass.TYPEDEF:
                            // Ignore.
                            return;

                        default:
                            throw new InvalidProgramException();
                    }

                    if (this.type.Kind != ExprTypeKind.FUNCTION) {
                        state.COMM(this.name, this.type.SizeOf, ExprType.ALIGN_LONG);
                    }

                    
                }

                state.NEWLINE();

            } else {
                // stack object

                state.CGenExpandStackTo(env.StackSize, ToString());

                Int32 stack_size = env.StackSize;

                // pos should be equal to stack_size, but whatever...
                Int32 pos = env.Find(this.name).Value.Offset;
                if (this.initr.IsNone) {
                    return;
                }

                Initr initr = this.initr.Value;
                initr.Iterate(this.type, (Int32 offset, Expr expr) => {
                    Reg ret = expr.CGenValue(state);
                    switch (expr.Type.Kind) {
                        case ExprTypeKind.CHAR:
                        case ExprTypeKind.UCHAR:
                            state.MOVB(Reg.EAX, pos + offset, Reg.EBP);
                            break;

                        case ExprTypeKind.SHORT:
                        case ExprTypeKind.USHORT:
                            state.MOVW(Reg.EAX, pos + offset, Reg.EBP);
                            break;

                        case ExprTypeKind.DOUBLE:
                            state.FSTPL(pos + offset, Reg.EBP);
                            break;

                        case ExprTypeKind.FLOAT:
                            state.FSTPS(pos + offset, Reg.EBP);
                            break;

                        case ExprTypeKind.LONG:
                        case ExprTypeKind.ULONG:
                        case ExprTypeKind.POINTER:
                            state.MOVL(Reg.EAX, pos + offset, Reg.EBP);
                            break;

                        case ExprTypeKind.STRUCT_OR_UNION:
                            state.MOVL(Reg.EAX, Reg.ESI);
                            state.LEA(pos + offset, Reg.EBP, Reg.EDI);
                            state.MOVL(expr.Type.SizeOf, Reg.ECX);
                            state.CGenMemCpy();
                            break;

                        case ExprTypeKind.ARRAY:
                        case ExprTypeKind.FUNCTION:
                            throw new InvalidProgramException($"How could a {expr.Type.Kind} be in a init list?");

                        default:
                            throw new InvalidProgramException();
                    }

                    state.CGenForceStackSizeTo(stack_size);

                });

            } // stack object
        }

        private readonly String name;
        private readonly StorageClass scs;
        private readonly ExprType type;
        private readonly Option<Initr> initr;
    }

    

    /// <summary>
    /// 1. Scalar: an expression, optionally enclosed in braces.
    ///    int a = 1;              // valid
    ///    int a = { 1 };          // valid
    ///    int a[] = { { 1 }, 2 }; // valid
    ///    int a = {{ 1 }};        // warning in gcc, a == 1; error in MSVC
    ///    int a = { { 1 }, 2 };   // warning in gcc, a == 1; error in MSVC
    ///    int a = { 1, 2 };       // warning in gcc, a == 1; error in MSVC
    ///    I'm following MSVC: you either put an expression, or add a single layer of brace.
    /// 
    /// 2. Union:
    ///    union A { int a; int b; };
    ///    union A u = { 1 };               // always initialize the first member, i.e. a, not b.
    ///    union A u = {{ 1 }};             // valid
    ///    union A u = another_union;       // valid
    /// 
    /// 3. Struct:
    ///    struct A { int a; int b; };
    ///    struct A = another_struct;       // valid
    ///    struct A = { another_struct };   // error, once you put a brace, the compiler assumes you want to initialize members.
    /// 
    /// From 2 and 3, once seen union or struct, either read expression or brace.
    /// 
    /// 4. Array of characters:
    ///    char a[] = { 'a', 'b' }; // valid
    ///    char a[] = "abc";        // becomes char a[4]: include '\0'
    ///    char a[3] = "abc";       // valid, ignore '\0'
    ///    char a[2] = "abc";       // warning in gcc; error in MSVC
    ///    If the aggregate contains members that are aggregates or unions, or if the first member of a union is an aggregate or union, the rules apply recursively to the subaggregates or contained unions. If the initializer of a subaggregate or contained union begins with a left brace, the initializers enclosed by that brace and its matching right brace initialize the members of the subaggregate or the first member of the contained union. Otherwise, only enough initializers from the list are taken to account for the members of the first subaggregate or the first member of the contained union; any remaining initializers are left to initialize the next member of the aggregate of which the current subaggregate or contained union is a part.
    /// </summary>
    public abstract class Initr {
        public enum Kind {
            EXPR,
            INIT_LIST
        }
        public abstract Kind kind { get; }

        public abstract Initr ConformType(MemberIterator iter);

        public Initr ConformType(ExprType type) => ConformType(new MemberIterator(type));

        public abstract void Iterate(MemberIterator iter, Action<Int32, Expr> action);

        public void Iterate(ExprType type, Action<Int32, Expr> action) => Iterate(new MemberIterator(type), action);
    }

    public class InitExpr : Initr {
        public InitExpr(Expr expr) {
            this.expr = expr;
        }
        public readonly Expr expr;
        public override Kind kind => Kind.EXPR;

        public override Initr ConformType(MemberIterator iter) {
            iter.Locate(this.expr.Type);
            Expr expr = TypeCast.MakeCast(this.expr, iter.CurType);
            return new InitExpr(expr);
        }

        public override void Iterate(MemberIterator iter, Action<Int32, Expr> action) {
            iter.Locate(this.expr.Type);
            Int32 offset = iter.CurOffset;
            Expr expr = this.expr;
            action(offset, expr);
        }
    }

    public class InitList : Initr {
        public InitList(List<Initr> initrs) {
            this.initrs = initrs;
        }
        public override Kind kind => Kind.INIT_LIST;
        public readonly List<Initr> initrs;

        public override Initr ConformType(MemberIterator iter) {
            iter.InBrace();
            List<Initr> initrs = new List<Initr>();
            for (Int32 i = 0; i < this.initrs.Count; ++i) {
                initrs.Add(this.initrs[i].ConformType(iter));
                if (i != this.initrs.Count - 1) {
                    iter.Next();
                }
            }
            iter.OutBrace();
            return new InitList(initrs);
        }

        public override void Iterate(MemberIterator iter, Action<Int32, Expr> action) {
            iter.InBrace();
            for (Int32 i = 0; i < this.initrs.Count; ++i) {
                this.initrs[i].Iterate(iter, action);
                if (i != this.initrs.Count - 1) {
                    iter.Next();
                }
            }
            iter.OutBrace();
        }
    }

    public class MemberIterator {
        public MemberIterator(ExprType type) {
            this.trace = new List<Status> { new Status(type) };
        }

        public class Status {
            public Status(ExprType base_type) {
                this.base_type = base_type;
                this.indices = new List<Int32>();
            }

            public ExprType CurType => GetType(this.base_type, this.indices);

            public Int32 CurOffset => GetOffset(this.base_type, this.indices);

            //public List<Tuple<ExprType, Int32>> GetPath(ExprType base_type, IReadOnlyList<Int32> indices) {
            //    ExprType Type = base_type;
            //    List<Tuple<ExprType, Int32>> path = new List<Tuple<ExprType, int>>();
            //    foreach (Int32 index in indices) {
            //        switch (Type.Kind) {
            //            case ExprType.Kind.ARRAY:
            //                Type = ((ArrayType)Type).ElemType;
            //                break;
            //            case ExprType.Kind.INCOMPLETE_ARRAY:
            //            case ExprType.Kind.STRUCT_OR_UNION:
            //            default:
            //                throw new InvalidProgramException("Not an aggregate Type.");
            //        }
            //    }
            //}

            public static ExprType GetType(ExprType from_type, Int32 to_index) {
                switch (from_type.Kind) {
                    case ExprTypeKind.ARRAY:
                        return ((ArrayType)from_type).ElemType;

                    case ExprTypeKind.INCOMPLETE_ARRAY:
                        return ((IncompleteArrayType)from_type).ElemType;

                    case ExprTypeKind.STRUCT_OR_UNION:
                        return ((StructOrUnionType)from_type).Attribs[to_index].type;

                    default:
                        throw new InvalidProgramException("Not an aggregate Type.");
                }
            }

            public static ExprType GetType(ExprType base_type, IReadOnlyList<Int32> indices) =>
                indices.Aggregate(base_type, GetType);

            public static Int32 GetOffset(ExprType from_type, Int32 to_index) {
                switch (from_type.Kind) {
                    case ExprTypeKind.ARRAY:
                        return to_index * ((ArrayType)from_type).ElemType.SizeOf;

                    case ExprTypeKind.INCOMPLETE_ARRAY:
                        return to_index * ((IncompleteArrayType)from_type).ElemType.SizeOf;

                    case ExprTypeKind.STRUCT_OR_UNION:
                        return ((StructOrUnionType)from_type).Attribs[to_index].offset;

                    default:
                        throw new InvalidProgramException("Not an aggregate Type.");
                }
            }

            public static Int32 GetOffset(ExprType base_type, IReadOnlyList<Int32> indices) {
                Int32 offset = 0;
                ExprType from_type = base_type;
                foreach (Int32 to_index in indices) {
                    offset += GetOffset(from_type, to_index);
                    from_type = GetType(from_type, to_index);
                }
                return offset;
            }

            public List<ExprType> GetTypes(ExprType base_type, IReadOnlyList<Int32> indices) {
                List<ExprType> types = new List<ExprType> { base_type };
                ExprType from_type = base_type;
                foreach (Int32 to_index in indices) {
                    from_type = GetType(from_type, to_index);
                    types.Add(from_type);
                }
                return types;
            }

            public void Next() {

                // From base_type to CurType.
                List<ExprType> types = GetTypes(this.base_type, this.indices);

                // We try to jump as many levels out as we can.
                do {
                    Int32 index = this.indices.Last();
                    this.indices.RemoveAt(this.indices.Count - 1);

                    types.RemoveAt(types.Count - 1);
                    ExprType type = types.Last();

                    switch (type.Kind) {
                        case ExprTypeKind.ARRAY:
                            if (index < ((ArrayType)type).NumElems - 1) {
                                // There are more elements in the array.
                                this.indices.Add(index + 1);
                                return;
                            }
                            break;

                        case ExprTypeKind.INCOMPLETE_ARRAY:
                            this.indices.Add(index + 1);
                            return;

                        case ExprTypeKind.STRUCT_OR_UNION:
                            if (((StructOrUnionType)type).IsStruct && index < ((StructOrUnionType)type).Attribs.Count - 1) {
                                // There are more members in the struct.
                                // (not union, since we can only initialize the first member of a union)
                                this.indices.Add(index + 1);
                                return;
                            }
                            break;

                        default:
                            break;
                    }

                } while (this.indices.Any());
            }

            /// <summary>
            /// Read an expression in the initializer list, locate the corresponding position.
            /// </summary>
            public void Locate(ExprType type) {
                switch (type.Kind) {
                    case ExprTypeKind.STRUCT_OR_UNION:
                        LocateStruct((StructOrUnionType)type);
                        return;
                    default:
                        // Even if the expression is of array Type, treat it as a scalar (pointer).
                        LocateScalar();
                        return;
                }
            }

            /// <summary>
            /// Try to match a scalar.
            /// This step doesn't check what scalar it is. Further steps would perform implicit conversions.
            /// </summary>
            private void LocateScalar() {
                while (!this.CurType.IsScalar) {
                    this.indices.Add(0);
                }
            }

            /// <summary>
            /// Try to match a given struct.
            /// Go down to find the first element of the same struct Type.
            /// </summary>
            private void LocateStruct(StructOrUnionType type) {
                while (!this.CurType.EqualType(type)) {
                    if (this.CurType.IsScalar) {
                        throw new InvalidOperationException("Trying to match a struct or union, but found a scalar.");
                    }

                    // Go down one level.
                    this.indices.Add(0);
                }
            }

            public readonly ExprType base_type;
            public readonly List<Int32> indices;
        }

        public ExprType CurType => this.trace.Last().CurType;

        public Int32 CurOffset => this.trace.Select(_ => _.CurOffset).Sum();

        public void Next() => this.trace.Last().Next();

        public void Locate(ExprType type) => this.trace.Last().Locate(type);

        public void InBrace() {

            /// Push the current position into the stack, so that we can get back by <see cref="OutBrace"/>
            this.trace.Add(new Status(this.trace.Last().CurType));

            // For aggregate types, go inside and locate the first member.
            if (!this.CurType.IsScalar) {
                this.trace.Last().indices.Add(0);
            }
            
        }

        public void OutBrace() => this.trace.RemoveAt(this.trace.Count - 1);

        public readonly List<Status> trace;
    }
}
