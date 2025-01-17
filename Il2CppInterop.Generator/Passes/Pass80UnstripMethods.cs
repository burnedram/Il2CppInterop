using System.Linq;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Utils;
using Microsoft.Extensions.Logging;
using Mono.Cecil;

namespace Il2CppInterop.Generator.Passes;

public static class Pass80UnstripMethods
{
    public static void DoPass(RewriteGlobalContext context)
    {
        var methodsUnstripped = 0;
        var methodsIgnored = 0;

        foreach (var unityAssembly in context.UnityAssemblies.Assemblies)
        {
            var processedAssembly = context.TryGetAssemblyByName(unityAssembly.Name.Name);
            if (processedAssembly == null) continue;
            var imports = processedAssembly.Imports;

            foreach (var unityType in unityAssembly.MainModule.Types)
            {
                var processedType = processedAssembly.TryGetTypeByName(unityType.FullName);
                if (processedType == null) continue;

                foreach (var unityMethod in unityType.Methods)
                {
                    var isICall = (unityMethod.ImplAttributes & MethodImplAttributes.InternalCall) != 0;
                    if (unityMethod.Name == ".cctor" || unityMethod.Name == ".ctor") continue;
                    if (unityMethod.IsAbstract) continue;
                    if (!unityMethod.HasBody && !isICall) continue; // CoreCLR chokes on no-body methods

                    var processedMethod = processedType.TryGetMethodByUnityAssemblyMethod(unityMethod);
                    if (processedMethod != null) continue;

                    var returnType = ResolveTypeInNewAssemblies(context, unityMethod.ReturnType, imports);
                    if (returnType == null)
                    {
                        Logger.Instance.LogTrace("Method {UnityMethod} has unsupported return type {UnityMethodReturnType}", unityMethod.ToString(), unityMethod.ReturnType.ToString());
                        methodsIgnored++;
                        continue;
                    }

                    var newMethod = new MethodDefinition(unityMethod.Name,
                        (unityMethod.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public,
                        returnType);
                    var hadBadParameter = false;
                    foreach (var unityMethodParameter in unityMethod.Parameters)
                    {
                        var convertedType =
                            ResolveTypeInNewAssemblies(context, unityMethodParameter.ParameterType, imports);
                        if (convertedType == null)
                        {
                            hadBadParameter = true;
                            Logger.Instance.LogTrace("Method {UnityMethod} has unsupported parameter type {UnityMethodParameter}", unityMethod.ToString(), unityMethodParameter.ToString());
                            break;
                        }

                        newMethod.Parameters.Add(new ParameterDefinition(unityMethodParameter.Name,
                            unityMethodParameter.Attributes, convertedType));
                    }

                    if (hadBadParameter)
                    {
                        methodsIgnored++;
                        continue;
                    }

                    foreach (var unityMethodGenericParameter in unityMethod.GenericParameters)
                    {
                        var newParameter = new GenericParameter(unityMethodGenericParameter.Name, newMethod);
                        newParameter.Attributes = unityMethodGenericParameter.Attributes;
                        foreach (var genericParameterConstraint in unityMethodGenericParameter.Constraints)
                        {
                            if (genericParameterConstraint.ConstraintType.FullName == "System.ValueType") continue;
                            if (genericParameterConstraint.ConstraintType.Resolve().IsInterface) continue;

                            var newType = ResolveTypeInNewAssemblies(context, genericParameterConstraint.ConstraintType,
                                imports);
                            if (newType != null)
                                newParameter.Constraints.Add(new GenericParameterConstraint(newType));
                        }

                        newMethod.GenericParameters.Add(newParameter);
                    }

                    if (isICall)
                    {
                        var delegateType =
                            UnstripGenerator.CreateDelegateTypeForICallMethod(unityMethod, newMethod, imports);
                        processedType.NewType.NestedTypes.Add(delegateType);
                        delegateType.DeclaringType = processedType.NewType;

                        processedType.NewType.Methods.Add(newMethod);

                        var delegateField = UnstripGenerator.GenerateStaticCtorSuffix(processedType.NewType,
                            delegateType, unityMethod, imports);
                        UnstripGenerator.GenerateInvokerMethodBody(newMethod, delegateField, delegateType,
                            processedType, imports);
                    }
                    else
                    {
                        Pass81FillUnstrippedMethodBodies.PushMethod(unityMethod, newMethod, processedType, imports);
                        processedType.NewType.Methods.Add(newMethod);
                    }

                    if (unityMethod.IsGetter)
                        GetOrCreateProperty(unityMethod, newMethod).GetMethod = newMethod;
                    else if (unityMethod.IsSetter)
                        GetOrCreateProperty(unityMethod, newMethod).SetMethod = newMethod;

                    var paramsMethod = context.CreateParamsMethod(unityMethod, newMethod, imports,
                        type => ResolveTypeInNewAssemblies(context, type, imports));
                    if (paramsMethod != null) processedType.NewType.Methods.Add(paramsMethod);

                    methodsUnstripped++;
                }
            }
        }

        Logger.Instance.LogInformation("Restored {UnstrippedMethods} methods", methodsUnstripped);
        Logger.Instance.LogInformation("Failed to restore {IgnoredMethods} methods", methodsIgnored);
    }

    private static PropertyDefinition GetOrCreateProperty(MethodDefinition unityMethod, MethodDefinition newMethod)
    {
        var unityProperty =
            unityMethod.DeclaringType.Properties.Single(
                it => it.SetMethod == unityMethod || it.GetMethod == unityMethod);
        var newProperty = newMethod.DeclaringType.Properties.SingleOrDefault(it =>
            it.Name == unityProperty.Name && it.Parameters.Count == unityProperty.Parameters.Count &&
            it.Parameters.SequenceEqual(unityProperty.Parameters, new TypeComparer()));
        if (newProperty == null)
        {
            newProperty = new PropertyDefinition(unityProperty.Name, PropertyAttributes.None,
                unityMethod.IsGetter ? newMethod.ReturnType : newMethod.Parameters.Last().ParameterType);
            newMethod.DeclaringType.Properties.Add(newProperty);
        }

        return newProperty;
    }

    internal static TypeReference? ResolveTypeInNewAssemblies(RewriteGlobalContext context, TypeReference unityType,
        RuntimeAssemblyReferences imports, bool resolveValueTypes = false)
    {
        return ResolveTypeInNewAssemblies(context, unityType, imports, out var _, resolveValueTypes);
    }

    internal static TypeReference? ResolveTypeInNewAssemblies(RewriteGlobalContext context, TypeReference unityType,
        RuntimeAssemblyReferences imports, out TypeRewriteContext rwContext, bool resolveValueTypes = false)
    {
        var resolved = ResolveTypeInNewAssembliesRaw(context, unityType, imports, out rwContext, resolveValueTypes);
        return resolved != null ? imports.Module.ImportReference(resolved) : null;
    }

    internal static TypeReference? ResolveTypeInNewAssembliesRaw(RewriteGlobalContext context, TypeReference unityType,
        RuntimeAssemblyReferences imports, bool resolveValueTypes = false)
    {
        return ResolveTypeInNewAssembliesRaw(context, unityType, imports, out var _, resolveValueTypes);
    }

    internal static TypeReference? ResolveTypeInNewAssembliesRaw(RewriteGlobalContext context, TypeReference unityType,
        RuntimeAssemblyReferences imports, out TypeRewriteContext rwContext, bool resolveValueTypes = false)
    {
        if (unityType is ByReferenceType unityRefType)
        {
            var resolvedElementType = ResolveTypeInNewAssemblies(context, unityRefType.ElementType, imports, out rwContext);
            return resolvedElementType == null ? null : new ByReferenceType(resolvedElementType);
        }

        if (unityType is GenericParameter)
        {
            rwContext = null;
            return null;
        }

        if (unityType is ArrayType arrayType)
        {
            if (arrayType.Rank != 1) { rwContext = null; return null; }
            var resolvedElementType = ResolveTypeInNewAssemblies(context, unityType.GetElementType(), imports, out rwContext, resolveValueTypes);
            if (resolvedElementType == null) { rwContext = null; return null; }
            if (resolvedElementType.FullName == "System.String")
                return imports.Il2CppStringArray;
            var genericBase = resolvedElementType.IsValueType
                ? imports.Il2CppStructArray
                : imports.Il2CppReferenceArray;
            return new GenericInstanceType(genericBase) { GenericArguments = { resolvedElementType } };
        }

        if (unityType.DeclaringType != null)
        {
            var enclosingResolvedType = ResolveTypeInNewAssembliesRaw(context, unityType.DeclaringType, imports, out rwContext, resolveValueTypes);
            if (enclosingResolvedType == null) return null;
            var resolvedNestedType = enclosingResolvedType.Resolve().NestedTypes
                .FirstOrDefault(it => it.Name == unityType.Name);
            if (resolvedNestedType == null) return null;
            return resolvedNestedType;
        }

        if (unityType is PointerType)
        {
            var resolvedElementType = ResolveTypeInNewAssemblies(context, unityType.GetElementType(), imports, out rwContext, resolveValueTypes);
            return resolvedElementType == null ? null : new PointerType(resolvedElementType);
        }

        if (unityType is GenericInstanceType genericInstance)
        {
            var baseRef = ResolveTypeInNewAssembliesRaw(context, genericInstance.ElementType, imports, out rwContext, resolveValueTypes);
            if (baseRef == null) return null;
            var newInstance = new GenericInstanceType(baseRef);
            foreach (var unityGenericArgument in genericInstance.GenericArguments)
            {
                var resolvedArgument = ResolveTypeInNewAssemblies(context, unityGenericArgument, imports);
                if (resolvedArgument == null) return null;
                newInstance.GenericArguments.Add(resolvedArgument);
            }

            return newInstance;
        }

        var targetAssemblyName = unityType.Scope.Name;
        if (targetAssemblyName.EndsWith(".dll"))
            targetAssemblyName = targetAssemblyName.Substring(0, targetAssemblyName.Length - 4);
        if ((targetAssemblyName == "mscorlib" || targetAssemblyName == "netstandard") && (
                (!resolveValueTypes && (unityType.IsValueType || unityType.FullName == "System.String")) ||
                 unityType.FullName == "System.Void" || unityType.IsPrimitive
             ) && unityType.FullName != "System.RuntimeTypeHandle")
        {
            rwContext = null;
            return imports.Module.ImportCorlibReference(unityType.Namespace, unityType.Name);
        }

        if (targetAssemblyName == "UnityEngine")
            foreach (var assemblyRewriteContext in context.Assemblies)
            {
                if (!assemblyRewriteContext.NewAssembly.Name.Name.StartsWith("UnityEngine")) continue;

                rwContext = assemblyRewriteContext.TryGetTypeByName(unityType.FullName);
                var newTypeInAnyUnityAssembly = rwContext?.NewType;
                if (newTypeInAnyUnityAssembly != null)
                    return newTypeInAnyUnityAssembly;
            }

        var targetAssembly = context.TryGetAssemblyByName(targetAssemblyName);
        rwContext = targetAssembly?.TryGetTypeByName(unityType.FullName);
        var newType = rwContext?.NewType;

        return newType;
    }

    //Stolen from: https://github.com/kremnev8/Il2CppInterop/blob/2c4a31f95f8aa6afe910aca0f8044efb80259d20/Il2CppInterop.Generator/Passes/Pass11ComputeTypeSpecifics.cs#L223
    internal sealed class TypeComparer : IEqualityComparer<ParameterDefinition>
    {
        public bool Equals(ParameterDefinition x, ParameterDefinition y)
        {
            if (x == null)
                return y == null;
            if (y == null)
                return false;

            return x.ParameterType.FullName.Equals(y.ParameterType.FullName);
        }

        public int GetHashCode(ParameterDefinition obj)
        {
            return obj.ParameterType.FullName.GetHashCode();
        }
    }
}
