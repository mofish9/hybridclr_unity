using System;
using System.Collections.Generic;
using System.Reflection;

namespace HybridCLR
{
    /// <summary>Identifies one closed method in a prewarm manifest.</summary>
    public sealed class RuntimePrewarmMethodDescriptor
    {
        public RuntimePrewarmMethodDescriptor(string declaringTypeName, string methodName,
            int parameterCount, int genericParameterCount, int metadataToken = 0,
            IEnumerable<string> parameterTypeNames = null, string returnTypeName = null)
        {
            DeclaringTypeName = declaringTypeName;
            MethodName = methodName;
            ParameterCount = parameterCount;
            GenericParameterCount = genericParameterCount;
            MetadataToken = metadataToken;
            ParameterTypeNames = parameterTypeNames == null
                ? null
                : new List<string>(parameterTypeNames).ToArray();
            ReturnTypeName = returnTypeName;
        }

        public string DeclaringTypeName { get; private set; }
        public string MethodName { get; private set; }
        public int ParameterCount { get; private set; }
        public int GenericParameterCount { get; private set; }
        public int MetadataToken { get; private set; }
        public string[] ParameterTypeNames { get; private set; }
        public string ReturnTypeName { get; private set; }
    }

    /// <summary>
    /// Resolves a generated type-name manifest after a hot-update assembly has
    /// been loaded and creates a budgeted prewarm queue for it.
    /// </summary>
    public static class RuntimePrewarmManifest
    {
        /// <summary>
        /// Creates a queue from type names belonging to <paramref name="assembly"/>.
        /// Missing or empty names are rejected so an incomplete manifest cannot be
        /// mistaken for a successful warmup.
        /// </summary>
        public static RuntimePrewarmQueue CreateQueue(Assembly assembly, IEnumerable<string> typeNames)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (typeNames == null)
                throw new ArgumentNullException("typeNames");

            var types = new List<Type>();
            foreach (string typeName in typeNames)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                    throw new ArgumentException("The prewarm manifest contains an empty type name.", "typeNames");

                Type type = ResolveType(assembly, typeName);
                if (type == null)
                    throw new InvalidOperationException("The prewarm manifest type was not found: " + typeName);
                types.Add(type);
            }
            return new RuntimePrewarmQueue(types);
        }

        /// <summary>
        /// Creates a queue that resolves manifest names incrementally. Unlike
        /// <see cref="CreateQueue"/>, this method does not enumerate or construct
        /// every type while the queue is being created. Drive the returned queue
        /// from a loading coroutine or Update loop so a large manifest cannot
        /// create a synchronous first-frame reflection spike.
        /// </summary>
        public static RuntimePrewarmManifestQueue CreateIncrementalQueue(Assembly assembly,
            IEnumerable<string> typeNames)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (typeNames == null)
                throw new ArgumentNullException("typeNames");
            return new RuntimePrewarmManifestQueue(assembly, typeNames);
        }

        /// <summary>
        /// Resolves an explicit method manifest and creates a budgeted method queue.
        /// Ambiguous or open-generic descriptors are rejected instead of being
        /// silently replaced with a broader class warmup.
        /// </summary>
        public static RuntimePrewarmMethodQueue CreateMethodQueue(Assembly assembly,
            IEnumerable<RuntimePrewarmMethodDescriptor> methodDescriptors)
        {
            var methods = ResolveMethodDescriptors(assembly, methodDescriptors);
            var methodInfos = new List<MethodInfo>(methods.Count);
            foreach (MethodBase method in methods)
            {
                MethodInfo methodInfo = method as MethodInfo;
                if (methodInfo == null)
                    throw new InvalidOperationException("The prewarm method manifest contains a constructor; use CreateMethodBaseQueue instead.");
                methodInfos.Add(methodInfo);
            }
            return new RuntimePrewarmMethodQueue(methodInfos);
        }

        /// <summary>Resolves a method graph that may include constructors.</summary>
        public static RuntimePrewarmMethodBaseQueue CreateMethodBaseQueue(Assembly assembly,
            IEnumerable<RuntimePrewarmMethodDescriptor> methodDescriptors)
        {
            return new RuntimePrewarmMethodBaseQueue(ResolveMethodDescriptors(assembly, methodDescriptors));
        }

        /// <summary>
        /// Creates a method/constructor queue whose descriptor resolution is
        /// deferred until <see cref="RuntimePrewarmMethodManifestQueue.Process"/>.
        /// Use this for large generated graphs to avoid one synchronous reflection
        /// pass immediately after Assembly.Load.
        /// </summary>
        public static RuntimePrewarmMethodManifestQueue CreateIncrementalMethodBaseQueue(
            Assembly assembly, IEnumerable<RuntimePrewarmMethodDescriptor> methodDescriptors)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (methodDescriptors == null)
                throw new ArgumentNullException("methodDescriptors");
            return new RuntimePrewarmMethodManifestQueue(assembly, methodDescriptors);
        }

        /// <summary>Creates a token-addressed queue without enumerating methods through reflection.</summary>
        public static RuntimePrewarmMethodTokenQueue CreateMethodTokenQueue(Assembly assembly,
            IEnumerable<RuntimePrewarmMethodDescriptor> methodDescriptors)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (methodDescriptors == null)
                throw new ArgumentNullException("methodDescriptors");

            var methods = new List<RuntimePrewarmMethodToken>();
            var typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (RuntimePrewarmMethodDescriptor descriptor in methodDescriptors)
            {
                if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.DeclaringTypeName) ||
                    descriptor.MetadataToken <= 0)
                    throw new ArgumentException("The prewarm token manifest contains an invalid descriptor.", "methodDescriptors");
                Type declaringType;
                if (!typeCache.TryGetValue(descriptor.DeclaringTypeName, out declaringType))
                {
                    declaringType = ResolveType(assembly, descriptor.DeclaringTypeName);
                    if (declaringType != null)
                        typeCache.Add(descriptor.DeclaringTypeName, declaringType);
                }
                if (declaringType == null)
                    throw new InvalidOperationException("The prewarm token declaring type was not found: " + descriptor.DeclaringTypeName);
                methods.Add(new RuntimePrewarmMethodToken(declaringType, descriptor.MetadataToken));
            }
            return new RuntimePrewarmMethodTokenQueue(methods);
        }

        /// <summary>
        /// Creates a token-addressed queue whose declaring types are resolved as
        /// entries are processed. This keeps large token manifests from doing a
        /// synchronous type-resolution pass immediately after Assembly.Load.
        /// </summary>
        public static RuntimePrewarmMethodTokenManifestQueue CreateIncrementalMethodTokenQueue(
            Assembly assembly, IEnumerable<RuntimePrewarmMethodDescriptor> methodDescriptors)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (methodDescriptors == null)
                throw new ArgumentNullException("methodDescriptors");
            return new RuntimePrewarmMethodTokenManifestQueue(assembly, methodDescriptors);
        }

        internal static MethodBase ResolveMethodDescriptor(Assembly assembly,
            RuntimePrewarmMethodDescriptor descriptor,
            Dictionary<Type, MethodBase[]> methodCache,
            Dictionary<Type, MethodBase[]> constructorCache,
            Dictionary<string, Type> typeCache)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.DeclaringTypeName) ||
                string.IsNullOrWhiteSpace(descriptor.MethodName))
                throw new ArgumentException("The prewarm method manifest contains an invalid descriptor.", "descriptor");
            if (descriptor.ParameterCount < 0 || descriptor.GenericParameterCount < 0)
                throw new ArgumentException("The prewarm method manifest contains a negative method count.", "descriptor");
            if (descriptor.GenericParameterCount != 0)
                throw new InvalidOperationException("Open generic methods require an explicit closed MethodInfo: " + descriptor.MethodName);

            Type declaringType;
            if (!typeCache.TryGetValue(descriptor.DeclaringTypeName, out declaringType))
            {
                declaringType = ResolveType(assembly, descriptor.DeclaringTypeName);
                if (declaringType != null)
                    typeCache.Add(descriptor.DeclaringTypeName, declaringType);
            }
            if (declaringType == null)
                throw new InvalidOperationException("The prewarm method declaring type was not found: " + descriptor.DeclaringTypeName);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            bool isConstructor = string.Equals(descriptor.MethodName, ".ctor", StringComparison.Ordinal);
            Dictionary<Type, MethodBase[]> candidateCache = isConstructor ? constructorCache : methodCache;
            MethodBase[] candidates;
            if (!candidateCache.TryGetValue(declaringType, out candidates))
            {
                candidates = isConstructor
                    ? declaringType.GetConstructors(flags)
                    : declaringType.GetMethods(flags);
                candidateCache.Add(declaringType, candidates);
            }
            MethodBase match = null;
            foreach (MethodBase candidate in candidates)
            {
                // Unity's IL2CPP ConstructorInfo does not implement the
                // abstract MethodBase.GetGenericArguments() contract.
                int genericParameterCount = candidate is MethodInfo
                    ? candidate.GetGenericArguments().Length
                    : 0;
                if (!string.Equals(candidate.Name, descriptor.MethodName, StringComparison.Ordinal) ||
                    genericParameterCount != descriptor.GenericParameterCount ||
                    candidate.GetParameters().Length != descriptor.ParameterCount ||
                    candidate.ContainsGenericParameters)
                    continue;
                if (descriptor.MetadataToken > 0 && GetMetadataToken(candidate) != descriptor.MetadataToken)
                    continue;
                if (!MatchesSignature(assembly, candidate, descriptor))
                    continue;
                if (match != null)
                    throw new InvalidOperationException("The prewarm method descriptor is ambiguous: " +
                        descriptor.DeclaringTypeName + "." + descriptor.MethodName);
                match = candidate;
            }
            if (match == null)
                throw new InvalidOperationException("The prewarm method was not found: " +
                    descriptor.DeclaringTypeName + "." + descriptor.MethodName);
            return match;
        }

        private static List<MethodBase> ResolveMethodDescriptors(Assembly assembly,
            IEnumerable<RuntimePrewarmMethodDescriptor> methodDescriptors)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            if (methodDescriptors == null)
                throw new ArgumentNullException("methodDescriptors");

            var methods = new List<MethodBase>();
            var typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
            var methodCache = new Dictionary<Type, MethodBase[]>();
            var constructorCache = new Dictionary<Type, MethodBase[]>();
            foreach (RuntimePrewarmMethodDescriptor descriptor in methodDescriptors)
                methods.Add(ResolveMethodDescriptor(assembly, descriptor, methodCache, constructorCache, typeCache));
            return methods;
        }

        private static int GetMetadataToken(MethodBase method)
        {
            try
            {
                return method.MetadataToken;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
            catch (NotSupportedException)
            {
                return 0;
            }
        }

        private static bool MatchesSignature(Assembly assembly, MethodBase method,
            RuntimePrewarmMethodDescriptor descriptor)
        {
            if (descriptor.ParameterTypeNames != null)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (descriptor.ParameterTypeNames.Length != parameters.Length)
                    throw new InvalidOperationException("The prewarm method signature has an inconsistent parameter count: " +
                        descriptor.DeclaringTypeName + "." + descriptor.MethodName);
                for (int index = 0; index < parameters.Length; index++)
                {
                    string parameterTypeName = descriptor.ParameterTypeNames[index];
                    if (string.IsNullOrWhiteSpace(parameterTypeName))
                        throw new InvalidOperationException("The prewarm method signature contains an empty parameter type: " +
                            descriptor.DeclaringTypeName + "." + descriptor.MethodName);
                    Type parameterType = ResolveType(assembly, parameterTypeName);
                    if (parameterType == null || parameterType != parameters[index].ParameterType)
                        return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(descriptor.ReturnTypeName))
            {
                Type returnType = ResolveType(assembly, descriptor.ReturnTypeName);
                if (returnType == null)
                    return false;
                MethodInfo methodInfo = method as MethodInfo;
                Type actualReturnType = methodInfo == null ? typeof(void) : methodInfo.ReturnType;
                if (actualReturnType != returnType)
                    return false;
            }
            return true;
        }

        internal static Type ResolveType(Assembly assembly, string typeName)
        {
            if (typeName.EndsWith("&", StringComparison.Ordinal))
            {
                Type elementType = ResolveType(assembly, typeName.Substring(0, typeName.Length - 1));
                return elementType == null ? null : elementType.MakeByRefType();
            }
            if (typeName.EndsWith("*", StringComparison.Ordinal))
            {
                Type elementType = ResolveType(assembly, typeName.Substring(0, typeName.Length - 1));
                return elementType == null ? null : elementType.MakePointerType();
            }
            if (typeName.EndsWith("]", StringComparison.Ordinal))
            {
                int arrayStart = typeName.LastIndexOf('[', typeName.Length - 1);
                if (arrayStart > 0)
                {
                    Type elementType = ResolveType(assembly, typeName.Substring(0, arrayStart));
                    if (elementType == null)
                        return null;
                    string rankText = typeName.Substring(arrayStart + 1, typeName.Length - arrayStart - 2);
                    if (rankText.Length == 0)
                        return elementType.MakeArrayType();
                    if (rankText == "*")
                        return elementType.MakeArrayType(1);
                    int rank = 1;
                    for (int index = 0; index < rankText.Length; index++)
                    {
                        if (rankText[index] != ',')
                            throw new FormatException("Malformed array rank in prewarm manifest: " + typeName);
                        rank++;
                    }
                    return elementType.MakeArrayType(rank);
                }
            }

            int genericStart = FindGenericStart(typeName);
            if (genericStart < 0)
                return ResolveNamedType(assembly, typeName);

            if (typeName[typeName.Length - 1] != '>')
                throw new FormatException("Malformed generic type name in prewarm manifest: " + typeName);

            string definitionName = typeName.Substring(0, genericStart);
            Type definition = ResolveNamedType(assembly, definitionName);
            if (definition == null)
                return null;
            if (!definition.IsGenericTypeDefinition)
                throw new InvalidOperationException("The prewarm manifest generic type is not a generic definition: " + definitionName);

            string argumentText = typeName.Substring(genericStart + 1, typeName.Length - genericStart - 2);
            string[] argumentNames = SplitGenericArguments(argumentText);
            int expectedArgumentCount = definition.GetGenericArguments().Length;
            if (argumentNames.Length != expectedArgumentCount)
                throw new InvalidOperationException("The prewarm manifest generic argument count does not match " +
                    definitionName + ": expected " + expectedArgumentCount + ", found " + argumentNames.Length + ".");
            var arguments = new Type[argumentNames.Length];
            for (int index = 0; index < argumentNames.Length; index++)
            {
                arguments[index] = ResolveType(assembly, argumentNames[index]);
                if (arguments[index] == null)
                    return null;
            }
            return definition.MakeGenericType(arguments);
        }

        private static Type ResolveNamedType(Assembly preferredAssembly, string typeName)
        {
            Type preferred = preferredAssembly.GetType(typeName, false) ?? ResolveCoreType(typeName) ?? Type.GetType(typeName, false);
            if (preferred != null)
                return preferred;

            Type resolved = null;
            foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = loadedAssembly.GetType(typeName, false);
                if (candidate == null)
                    continue;
                if (resolved != null && resolved != candidate)
                    throw new InvalidOperationException("The prewarm manifest type name is ambiguous across loaded assemblies: " + typeName);
                resolved = candidate;
            }
            return resolved;
        }

        private static Type ResolveCoreType(string typeName)
        {
            switch (typeName)
            {
                case "System.Void": return typeof(void);
                case "System.Boolean": return typeof(bool);
                case "System.Char": return typeof(char);
                case "System.SByte": return typeof(sbyte);
                case "System.Byte": return typeof(byte);
                case "System.Int16": return typeof(short);
                case "System.UInt16": return typeof(ushort);
                case "System.Int32": return typeof(int);
                case "System.UInt32": return typeof(uint);
                case "System.Int64": return typeof(long);
                case "System.UInt64": return typeof(ulong);
                case "System.Single": return typeof(float);
                case "System.Double": return typeof(double);
                case "System.String": return typeof(string);
                case "System.Object": return typeof(object);
                case "System.IntPtr": return typeof(IntPtr);
                case "System.UIntPtr": return typeof(UIntPtr);
                case "System.Decimal": return typeof(decimal);
                default: return null;
            }
        }

        private static int FindGenericStart(string typeName)
        {
            int depth = 0;
            int genericStart = -1;
            for (int index = 0; index < typeName.Length; index++)
            {
                if (typeName[index] == '<')
                {
                    if (depth++ == 0)
                        genericStart = index;
                }
                else if (typeName[index] == '>')
                {
                    if (depth == 0)
                        throw new FormatException("Malformed generic type name in prewarm manifest: " + typeName);
                    depth--;
                    if (depth == 0 && index != typeName.Length - 1)
                        throw new FormatException("Unexpected text after generic arguments in prewarm manifest: " + typeName);
                }
            }
            if (depth != 0)
                throw new FormatException("Unbalanced generic type name in prewarm manifest: " + typeName);
            return genericStart;
        }

        private static string[] SplitGenericArguments(string argumentText)
        {
            var arguments = new List<string>();
            int angleDepth = 0;
            int squareDepth = 0;
            int start = 0;
            for (int index = 0; index < argumentText.Length; index++)
            {
                char character = argumentText[index];
                if (character == '<')
                    angleDepth++;
                else if (character == '>')
                    angleDepth--;
                else if (character == '[')
                    squareDepth++;
                else if (character == ']')
                    squareDepth--;
                else if (character == ',' && angleDepth == 0 && squareDepth == 0)
                {
                    arguments.Add(argumentText.Substring(start, index - start).Trim());
                    start = index + 1;
                }
            }
            if (angleDepth != 0 || squareDepth != 0)
                throw new FormatException("Unbalanced generic argument list in prewarm manifest: " + argumentText);
            arguments.Add(argumentText.Substring(start).Trim());
            for (int index = 0; index < arguments.Count; index++)
            {
                if (arguments[index].Length == 0)
                    throw new FormatException("The prewarm manifest contains an empty generic argument: " + argumentText);
            }
            return arguments.ToArray();
        }
    }
}
