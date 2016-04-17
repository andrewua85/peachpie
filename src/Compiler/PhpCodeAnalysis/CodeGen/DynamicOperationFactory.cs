﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeGen;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Pchp.CodeAnalysis.CodeGen
{
    internal class DynamicOperationFactory
    {
        public class CallSiteData
        {
            /// <summary>
            /// CallSite_T.Target method.
            /// </summary>
            public FieldSymbol Target => _target;
            SubstitutedFieldSymbol _target;

            /// <summary>
            /// CallSite_T field.
            /// </summary>
            public IPlace Place => new FieldPlace(null, _fld);
            SynthesizedFieldSymbol _fld;

            /// <summary>
            /// Gets CallSite.Create method.
            /// </summary>
            public MethodSymbol CallSite_Create => _callsite_create;
            MethodSymbol _callsite_create;

            public void Construct(NamedTypeSymbol functype)
            {
                var callsitetype = _factory.CallSite_T.Construct(functype);

                _target.SetContainingType((SubstitutedNamedTypeSymbol)callsitetype);
                _fld.SetFieldType(callsitetype);
                _callsite_create = (MethodSymbol)_factory.CallSite_T_Create.SymbolAsMember(callsitetype);
            }

            readonly DynamicOperationFactory _factory;

            internal CallSiteData(DynamicOperationFactory factory, string fldname = null)
            {
                _factory = factory;

                _target = new SubstitutedFieldSymbol(factory.CallSite_T, factory.CallSite_T_Target); // AsMember // we'll change containing type later once we know, important to have Substitued symbol before calling it
                _fld = factory.CreateCallSiteField(fldname ?? string.Empty);
            }
        }

        readonly PhpCompilation _compilation;
        readonly NamedTypeSymbol _container;

        NamedTypeSymbol _callsitetype;
        NamedTypeSymbol _callsitetype_generic;
        MethodSymbol _callsite_generic_create;
        FieldSymbol _callsite_generic_target;

        public NamedTypeSymbol CallSite => _callsitetype ?? (_callsitetype = _compilation.GetWellKnownType(WellKnownType.System_Runtime_CompilerServices_CallSite));
        public NamedTypeSymbol CallSite_T => _callsitetype_generic ?? (_callsitetype_generic = _compilation.GetWellKnownType(WellKnownType.System_Runtime_CompilerServices_CallSite_T));
        public MethodSymbol CallSite_T_Create => _callsite_generic_create ?? (_callsite_generic_create = (MethodSymbol)_compilation.GetWellKnownTypeMember(WellKnownMember.System_Runtime_CompilerServices_CallSite_T__Create));
        public FieldSymbol CallSite_T_Target => _callsite_generic_target ?? (_callsite_generic_target = (FieldSymbol)_compilation.GetWellKnownTypeMember(WellKnownMember.System_Runtime_CompilerServices_CallSite_T__Target));

        public CallSiteData StartCallSite(string fldname) => new CallSiteData(this, fldname);

        /// <summary>
        /// Static constructor IL builder for dynamic sites in current context.
        /// </summary>
        public ILBuilder CctorBuilder => ((Emit.PEModuleBuilder)_container.ContainingModule).GetStaticCtorBuilder(_container);

        int _fieldIndex;

        public SynthesizedFieldSymbol CreateCallSiteField(string namehint)
            => ((IWithSynthesized)_container).CreateSynthesizedField(CallSite, "<>" + namehint + "'" + (_fieldIndex++), Accessibility.Private, true);

        public DynamicOperationFactory(PhpCompilation compilation, NamedTypeSymbol container)
        {
            Contract.ThrowIfNull(compilation);
            Contract.ThrowIfNull(container);

            Debug.Assert(container is IWithSynthesized);

            _compilation = compilation;
            _container = container;
        }

        internal NamedTypeSymbol GetCallSiteDelegateType(
            TypeSymbol loweredReceiver,
            RefKind receiverRefKind,
            ImmutableArray<TypeSymbol> loweredArguments,
            ImmutableArray<RefKind> refKinds,
            TypeSymbol loweredRight,
            TypeSymbol resultType)
        {
            Debug.Assert(refKinds.IsDefaultOrEmpty || refKinds.Length == loweredArguments.Length);

            var callSiteType = _compilation.GetWellKnownType(WellKnownType.System_Runtime_CompilerServices_CallSite);
            if (callSiteType.IsErrorType())
            {
                return null;
            }

            var delegateSignature = MakeCallSiteDelegateSignature(callSiteType, loweredReceiver, loweredArguments, loweredRight, resultType);
            bool returnsVoid = resultType.SpecialType == SpecialType.System_Void;
            bool hasByRefs = receiverRefKind != RefKind.None || !refKinds.IsDefaultOrEmpty;

            if (!hasByRefs)
            {
                var wkDelegateType = returnsVoid ?
                    WellKnownTypes.GetWellKnownActionDelegate(invokeArgumentCount: delegateSignature.Length) :
                    WellKnownTypes.GetWellKnownFunctionDelegate(invokeArgumentCount: delegateSignature.Length - 1);

                if (wkDelegateType != WellKnownType.Unknown)
                {
                    var delegateType = _compilation.GetWellKnownType(wkDelegateType);
                    if (delegateType != null && !delegateType.IsErrorType())
                    {
                        return delegateType.Construct(delegateSignature);
                    }
                }
            }

            BitVector byRefs;
            if (hasByRefs)
            {
                byRefs = BitVector.Create(1 + (loweredReceiver != null ? 1 : 0) + loweredArguments.Length + (loweredRight != null ? 1 : 0));

                int j = 1;
                if (loweredReceiver != null)
                {
                    byRefs[j++] = receiverRefKind != RefKind.None;
                }

                if (!refKinds.IsDefault)
                {
                    for (int i = 0; i < refKinds.Length; i++, j++)
                    {
                        if (refKinds[i] != RefKind.None)
                        {
                            byRefs[j] = true;
                        }
                    }
                }
            }
            else
            {
                byRefs = default(BitVector);
            }

            int parameterCount = delegateSignature.Length - (returnsVoid ? 0 : 1);

            return _compilation.AnonymousTypeManager.SynthesizeDelegate(parameterCount, byRefs, returnsVoid).Construct(delegateSignature);
        }

        internal TypeSymbol[] MakeCallSiteDelegateSignature(TypeSymbol callSiteType, TypeSymbol receiver, ImmutableArray<TypeSymbol> arguments, TypeSymbol right, TypeSymbol resultType)
        {
            var systemObjectType = (TypeSymbol)_compilation.GetSpecialType(SpecialType.System_Object);
            var result = new TypeSymbol[1 + (receiver != null ? 1 : 0) + arguments.Length + (right != null ? 1 : 0) + (resultType.SpecialType == SpecialType.System_Void ? 0 : 1)];
            int j = 0;

            // CallSite:
            result[j++] = callSiteType;

            // receiver:
            if (receiver != null)
            {
                result[j++] = receiver;
            }

            // argument types:
            for (int i = 0; i < arguments.Length; i++)
            {
                result[j++] = arguments[i];
            }

            // right hand side of an assignment:
            if (right != null)
            {
                result[j++] = right;
            }

            // return type:
            if (j < result.Length)
            {
                result[j++] = resultType;
            }

            return result;
        }
    }
}
