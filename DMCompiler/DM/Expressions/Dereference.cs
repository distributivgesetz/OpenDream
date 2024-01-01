using DMCompiler.Bytecode;
using DMCompiler.Compiler.DM;
using OpenDreamShared.Compiler;
using OpenDreamShared.Dream;
using System;
using System.Diagnostics.CodeAnalysis;

namespace DMCompiler.DM.Expressions {
    // x.y.z
    // x[y][z]
    // x.f().y.g()[2]
    // etc.
    class Dereference : LValue {
        public struct Operation {
            public DMASTDereference.OperationKind Kind;

            // Field*, Call*
            public string Identifier;

            // Field*
            public int? GlobalId;

            // Index*
            public DMExpression Index;

            // Call*
            public ArgumentList Parameters;

            public DreamPath? Path;
        }

        private readonly DMExpression _expression;
        private readonly Operation[] _operations;

        public override DreamPath? Path { get; }
        public override DreamPath? NestedPath { get; }
        public override bool IsFuzzy => Path == null;

        public Dereference(Location location, DreamPath? path, DMExpression expression, Operation[] operations)
            : base(location, null) {
            _expression = expression;
            _operations = operations;
            Path = path;

            if (_operations.Length == 0) {
                throw new InvalidOperationException("deref expression has no operations");
            }

            NestedPath = _operations[^1].Path;
        }

        private void ShortCircuitHandler(DMProc proc, string endLabel, ShortCircuitMode shortCircuitMode) {
            switch (shortCircuitMode) {
                case ShortCircuitMode.PopNull:
                    proc.JumpIfNull(endLabel);
                    break;
                case ShortCircuitMode.KeepNull:
                    proc.JumpIfNullNoPop(endLabel);
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        private void EmitOperation(DMObject dmObject, DMProc proc, ref Operation operation, string endLabel, ShortCircuitMode shortCircuitMode) {
            switch (operation.Kind) {
                case DMASTDereference.OperationKind.Field:
                case DMASTDereference.OperationKind.FieldSearch:
                    proc.DereferenceField(operation.Identifier);
                    break;

                case DMASTDereference.OperationKind.FieldSafe:
                case DMASTDereference.OperationKind.FieldSafeSearch:
                    ShortCircuitHandler(proc, endLabel, shortCircuitMode);
                    proc.DereferenceField(operation.Identifier);
                    break;

                case DMASTDereference.OperationKind.Index:
                    operation.Index.EmitPushValue(dmObject, proc);
                    proc.DereferenceIndex();
                    break;

                case DMASTDereference.OperationKind.IndexSafe:
                    ShortCircuitHandler(proc, endLabel, shortCircuitMode);
                    operation.Index.EmitPushValue(dmObject, proc);
                    proc.DereferenceIndex();
                    break;

                case DMASTDereference.OperationKind.Call:
                case DMASTDereference.OperationKind.CallSearch: {
                    var (argumentsType, argumentStackSize) = operation.Parameters.EmitArguments(dmObject, proc);
                    proc.DereferenceCall(operation.Identifier, argumentsType, argumentStackSize);
                    break;
                }

                case DMASTDereference.OperationKind.CallSafe:
                case DMASTDereference.OperationKind.CallSafeSearch: {
                    ShortCircuitHandler(proc, endLabel, shortCircuitMode);
                    var (argumentsType, argumentStackSize) = operation.Parameters.EmitArguments(dmObject, proc);
                    proc.DereferenceCall(operation.Identifier, argumentsType, argumentStackSize);
                    break;
                }

                case DMASTDereference.OperationKind.Invalid:
                default:
                    throw new NotImplementedException();
            };
        }

        public override void EmitPushValue(DMObject dmObject, DMProc proc) {
            string endLabel = proc.NewLabelName();

            _expression.EmitPushValue(dmObject, proc);

            foreach (ref var operation in _operations.AsSpan()) {
                EmitOperation(dmObject, proc, ref operation, endLabel, ShortCircuitMode.KeepNull);
            }

            proc.AddLabel(endLabel);
        }

        public override bool CanReferenceShortCircuit() {
            foreach (var operation in _operations) {
                switch (operation.Kind) {
                    case DMASTDereference.OperationKind.FieldSafe:
                    case DMASTDereference.OperationKind.FieldSafeSearch:
                    case DMASTDereference.OperationKind.IndexSafe:
                    case DMASTDereference.OperationKind.CallSafe:
                    case DMASTDereference.OperationKind.CallSafeSearch:
                        return true;

                    case DMASTDereference.OperationKind.Field:
                    case DMASTDereference.OperationKind.FieldSearch:
                    case DMASTDereference.OperationKind.Index:
                    case DMASTDereference.OperationKind.Call:
                    case DMASTDereference.OperationKind.CallSearch:
                        break;

                    case DMASTDereference.OperationKind.Invalid:
                    default:
                        throw new NotImplementedException();
                }
            }

            return base.CanReferenceShortCircuit();
        }

        public override DMReference EmitReference(DMObject dmObject, DMProc proc, string endLabel, ShortCircuitMode shortCircuitMode) {
            _expression.EmitPushValue(dmObject, proc);

            // Perform all except for our last operation
            for (int i = 0; i < _operations.Length - 1; i++) {
                EmitOperation(dmObject, proc, ref _operations[i], endLabel, shortCircuitMode);
            }

            ref var operation = ref _operations[^1];

            switch (operation.Kind) {
                case DMASTDereference.OperationKind.Field:
                case DMASTDereference.OperationKind.FieldSearch:
                    return DMReference.CreateField(operation.Identifier);

                case DMASTDereference.OperationKind.FieldSafe:
                case DMASTDereference.OperationKind.FieldSafeSearch:
                    ShortCircuitHandler(proc, endLabel, shortCircuitMode);
                    return DMReference.CreateField(operation.Identifier);

                case DMASTDereference.OperationKind.Index:
                    operation.Index.EmitPushValue(dmObject, proc);
                    return DMReference.ListIndex;

                case DMASTDereference.OperationKind.IndexSafe:
                    ShortCircuitHandler(proc, endLabel, shortCircuitMode);
                    operation.Index.EmitPushValue(dmObject, proc);
                    return DMReference.ListIndex;

                case DMASTDereference.OperationKind.Call:
                case DMASTDereference.OperationKind.CallSearch:
                case DMASTDereference.OperationKind.CallSafe:
                case DMASTDereference.OperationKind.CallSafeSearch:
                    throw new CompileErrorException(Location, $"attempt to reference proc call result");

                case DMASTDereference.OperationKind.Invalid:
                default:
                    throw new NotImplementedException();
            };
        }

        public override void EmitPushInitial(DMObject dmObject, DMProc proc) {
            string endLabel = proc.NewLabelName();

            _expression.EmitPushValue(dmObject, proc);

            // Perform all except for our last operation
            for (int i = 0; i < _operations.Length - 1; i++) {
                EmitOperation(dmObject, proc, ref _operations[i], endLabel, ShortCircuitMode.KeepNull);
            }

            ref var operation = ref _operations[^1];

            switch (operation.Kind) {
                case DMASTDereference.OperationKind.Field:
                case DMASTDereference.OperationKind.FieldSearch:
                    proc.PushString(operation.Identifier);
                    proc.Initial();
                    break;

                case DMASTDereference.OperationKind.FieldSafe:
                case DMASTDereference.OperationKind.FieldSafeSearch:
                    proc.JumpIfNullNoPop(endLabel);
                    proc.PushString(operation.Identifier);
                    proc.Initial();
                    break;

                case DMASTDereference.OperationKind.Index:
                    operation.Index.EmitPushValue(dmObject, proc);
                    proc.Initial();
                    break;

                case DMASTDereference.OperationKind.IndexSafe:
                    proc.JumpIfNullNoPop(endLabel);
                    operation.Index.EmitPushValue(dmObject, proc);
                    proc.Initial();
                    break;

                case DMASTDereference.OperationKind.Call:
                case DMASTDereference.OperationKind.CallSearch:
                case DMASTDereference.OperationKind.CallSafe:
                case DMASTDereference.OperationKind.CallSafeSearch:
                    throw new CompileErrorException(Location, $"attempt to get `initial` of a proc call");

                case DMASTDereference.OperationKind.Invalid:
                default:
                    throw new NotImplementedException();
            };

            proc.AddLabel(endLabel);
        }

        public void EmitPushIsSaved(DMObject dmObject, DMProc proc) {
            string endLabel = proc.NewLabelName();

            _expression.EmitPushValue(dmObject, proc);

            // Perform all except for our last operation
            for (int i = 0; i < _operations.Length - 1; i++) {
                EmitOperation(dmObject, proc, ref _operations[i], endLabel, ShortCircuitMode.KeepNull);
            }

            ref var operation = ref _operations[^1];

            switch (operation.Kind) {
                case DMASTDereference.OperationKind.Field:
                case DMASTDereference.OperationKind.FieldSearch:
                    proc.PushString(operation.Identifier);
                    proc.IsSaved();
                    break;

                case DMASTDereference.OperationKind.FieldSafe:
                case DMASTDereference.OperationKind.FieldSafeSearch:
                    proc.JumpIfNullNoPop(endLabel);
                    proc.PushString(operation.Identifier);
                    proc.IsSaved();
                    break;

                case DMASTDereference.OperationKind.Index:
                    operation.Index.EmitPushValue(dmObject, proc);
                    proc.IsSaved();
                    break;

                case DMASTDereference.OperationKind.IndexSafe:
                    proc.JumpIfNullNoPop(endLabel);
                    operation.Index.EmitPushValue(dmObject, proc);
                    proc.IsSaved();
                    break;

                case DMASTDereference.OperationKind.Call:
                case DMASTDereference.OperationKind.CallSearch:
                case DMASTDereference.OperationKind.CallSafe:
                case DMASTDereference.OperationKind.CallSafeSearch:
                    throw new CompileErrorException(Location, $"attempt to get `issaved` of a proc call");

                case DMASTDereference.OperationKind.Invalid:
                default:
                    throw new NotImplementedException();
            };

            proc.AddLabel(endLabel);
        }

        public override bool TryAsConstant(out Constant constant) {
            var prevPath = _operations.Length == 1 ? _expression.Path : _operations[^2].Path;

            ref var operation = ref _operations[^1];

            switch (operation.Kind) {
                case DMASTDereference.OperationKind.Field:
                case DMASTDereference.OperationKind.FieldSearch:
                case DMASTDereference.OperationKind.FieldSafe:
                case DMASTDereference.OperationKind.FieldSafeSearch:
                    if (prevPath is not null) {
                        var obj = DMObjectTree.GetDMObject(prevPath.GetValueOrDefault());
                        var variable = obj.GetVariable(operation.Identifier);
                        if (variable != null) {
                            if (variable.IsConst)
                                return variable.Value.TryAsConstant(out constant);
                            if ((variable.ValType & DMValueType.CompiletimeReadonly) == DMValueType.CompiletimeReadonly) {
                                variable.Value.TryAsConstant(out constant);
                                return true; // MUST be true.
                            }
                        }
                    }
                    break;
            }

            constant = null;
            return false;

        }
    }

    // expression::identifier
    internal sealed class ScopeReference : LValue {
        private readonly DMExpression _expression;
        private readonly string _identifier;

        public ScopeReference(Location location, DMExpression expression, string identifier)
            : base(location, expression.Path) {
            _expression = expression;
            _identifier = identifier;
        }

        public override void EmitPushValue(DMObject dmObject, DMProc proc) {
            var type = _expression.Path.HasValue ? DMObjectTree.GetDMObject(_expression.Path.Value, false) : null;

            // this is a developer error, the method that created this object should have verified that it's valid
            if (type is null) {
                throw new InvalidOperationException("Typeless left-hand expression is not accepted here");
            }

            DMVariable? variable = type.GetVariable(_identifier) ?? type.GetGlobalVariable(_identifier);
            if (variable is null) {
                throw new InvalidOperationException($"Type {_expression.Path} does not contain variable {_identifier}");
            }

            if (variable.IsGlobal) {
                proc.PushReferenceValue(DMReference.CreateGlobal(type.GetGlobalVariableId(_identifier)!.Value));
            } else {
                _expression.EmitPushValue(dmObject, proc);
                proc.PushString(_identifier);
                proc.Initial();
            }
        }

        public override string GetNameof() => _identifier;

        public override bool TryAsConstant([NotNullWhen(true)] out Constant? constant) {
            constant = null;
            if (!Path.HasValue || DMObjectTree.GetDMObject(Path.Value) is not { } varObject) {
                return false;
            }

            var variable = varObject.GetVariable(_identifier);
            if (variable is null) {
                throw new InvalidOperationException($"Type {_expression.Path} does not contain variable {_identifier}");
            }

            return variable.Value.TryAsConstant(out constant);
        }
    }
}
