using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace Tl.Unity.CodeGen
{
    public sealed class TypeRegistrationPostProcessor : ILPostProcessor
    {
        const string RuntimeAssembly = "Tl.Unity";
        const string RegistrationNamespace = "TlUnityGenerated";
        const string RegistrationPrefix = "TlUnityTypeRegistration_";
        const string RegistrationMethod = "Register";
        const string InitializationType = "__UnityModuleInitialization";
        const string InitializationMethod = "Initialize";

        public override ILPostProcessor GetInstance()
        {
            return this;
        }

        public override bool WillProcess(ICompiledAssembly compiledAssembly)
        {
            foreach (var reference in compiledAssembly.References)
            {
                var name = Path.GetFileNameWithoutExtension(reference);
                if (name == RuntimeAssembly)
                    return true;
            }
            return false;
        }

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            if (!WillProcess(compiledAssembly))
                return null;
            using var resolver = new DefaultAssemblyResolver();
            foreach (var directory in compiledAssembly.References.Select(Path.GetDirectoryName).Distinct())
                resolver.AddSearchDirectory(directory);
            var reader = new ReaderParameters
            {
                SymbolStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PdbData.ToArray()),
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                AssemblyResolver = resolver,
                ReadingMode = ReadingMode.Immediate
            };
            using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(compiledAssembly.InMemoryAssembly.PeData.ToArray()), reader);
            var module = assembly.MainModule;
            var register = FindRegistration(module);
            if (register == null)
                return new ILPostProcessResult(null, new List<DiagnosticMessage>());
            var initialize = InitializeOf(module);
            foreach (var instruction in initialize.Body.Instructions)
                if (instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference called && called.FullName == register.FullName)
                    return new ILPostProcessResult(null, new List<DiagnosticMessage>());
            var il = initialize.Body.GetILProcessor();
            il.InsertBefore(initialize.Body.Instructions[0], il.Create(OpCodes.Call, register));
            var pe = new MemoryStream();
            var pdb = new MemoryStream();
            assembly.Write(pe, new WriterParameters { SymbolWriterProvider = new PortablePdbWriterProvider(), SymbolStream = pdb, WriteSymbols = true });
            return new ILPostProcessResult(new InMemoryAssembly(pe.ToArray(), pdb.ToArray()), new List<DiagnosticMessage>());
        }

        static MethodDefinition FindRegistration(ModuleDefinition module)
        {
            foreach (var type in module.Types)
            {
                if (type.Namespace != RegistrationNamespace || !type.Name.StartsWith(RegistrationPrefix, StringComparison.Ordinal))
                    continue;
                foreach (var method in type.Methods)
                    if (method.Name == RegistrationMethod && method.IsStatic && !method.HasParameters)
                        return method;
            }
            return null;
        }

        static MethodDefinition InitializeOf(ModuleDefinition module)
        {
            var type = module.GetType(InitializationType);
            if (type == null)
            {
                type = new TypeDefinition("", InitializationType, TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
                Preserve(module, type.CustomAttributes);
                module.Types.Add(type);
            }
            foreach (var method in type.Methods)
                if (method.Name == InitializationMethod && method.IsStatic && !method.HasParameters)
                    return method;
            var initialize = new MethodDefinition(InitializationMethod, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            Preserve(module, initialize.CustomAttributes);
            initialize.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(initialize);
            return initialize;
        }

        static void Preserve(ModuleDefinition module, Mono.Collections.Generic.Collection<CustomAttribute> attributes)
        {
            foreach (var reference in module.AssemblyReferences)
            {
                if (reference.Name != "UnityEngine.CoreModule")
                    continue;
                var attribute = new TypeReference("UnityEngine.Scripting", "PreserveAttribute", module, reference);
                attributes.Add(new CustomAttribute(new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true }));
                return;
            }
        }
    }
}
