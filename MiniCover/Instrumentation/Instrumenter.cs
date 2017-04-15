﻿using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MiniCover.Instrumentation
{
    public class Instrumenter
    {
        private int id = 0;
        private IList<string> assemblies;
        private string hitsFile;
        private IList<string> sourceFiles;
        private string normalizedWorkDir;

        private InstrumentationResult result;

        public Instrumenter(IList<string> assemblies, string hitsFile, IList<string> sourceFiles, string workdir)
        {
            this.assemblies = assemblies;
            this.hitsFile = hitsFile;
            this.sourceFiles = sourceFiles;

            normalizedWorkDir = workdir;
            if (!normalizedWorkDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                normalizedWorkDir += Path.DirectorySeparatorChar;
        }

        public InstrumentationResult Execute()
        {
            id = 0;

            result = new InstrumentationResult
            {
                SourcePath = normalizedWorkDir,
                HitsFile = hitsFile
            };

            foreach (var fileName in assemblies)
            {
                InstrumentAssembly(fileName);
            }

            result.Files = result.Files.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);

            return result;
        }

        private void InstrumentAssembly(string assemblyFile)
        {
            var pdbFile = Path.ChangeExtension(assemblyFile, "pdb");
            if (!File.Exists(pdbFile))
                return;

            var backupFile = $"{assemblyFile}.original";
            if (File.Exists(backupFile))
                File.Copy(backupFile, assemblyFile, true);

            if (!HasSourceFiles(assemblyFile))
                return;

            if (IsInstrumented(assemblyFile))
                throw new Exception($"Assembly file ${assemblyFile} is already instrumented");

            File.Copy(assemblyFile, backupFile, true);
            result.AddInstrumentedAssembly(Path.GetFullPath(backupFile), Path.GetFullPath(assemblyFile));

            var assemblyDirectory = Path.GetDirectoryName(assemblyFile);
            byte[] bytes = null;

            using (var assemblyDefinition = AssemblyDefinition.ReadAssembly(assemblyFile, new ReaderParameters { ReadSymbols = true }))
            {
                var instrumentedConstructor = typeof(InstrumentedAttribute).GetConstructors().First();
                var instrumentedReference = assemblyDefinition.MainModule.ImportReference(instrumentedConstructor);
                assemblyDefinition.CustomAttributes.Add(new CustomAttribute(instrumentedReference));

                var miniCoverAssemblyPath = typeof(HitService).GetTypeInfo().Assembly.Location;
                var miniCoverAssemblyName = Path.GetFileName(miniCoverAssemblyPath);
                var newMiniCoverAssemblyPath = Path.Combine(assemblyDirectory, miniCoverAssemblyName);
                File.Copy(miniCoverAssemblyPath, newMiniCoverAssemblyPath, true);
                result.AddExtraAssembly(newMiniCoverAssemblyPath);

                CreateAssemblyInit(assemblyDefinition);

                var hitMethodInfo = typeof(HitService).GetMethod("Hit");
                var hitMethodReference = assemblyDefinition.MainModule.ImportReference(hitMethodInfo);

                foreach (var type in assemblyDefinition.MainModule.GetTypes())
                {
                    InstrumentType(type, hitMethodReference);
                }

                using (var memoryStream = new MemoryStream())
                {
                    assemblyDefinition.Write(memoryStream);
                    bytes = memoryStream.ToArray();
                }
            }

            if (bytes != null)
            {
                File.WriteAllBytes(assemblyFile, bytes);
            }
        }

        private void CreateAssemblyInit(AssemblyDefinition assemblyDefinition)
        {
            var initMethodInfo = typeof(HitService).GetMethod("Init");
            var initMethodReference = assemblyDefinition.MainModule.ImportReference(initMethodInfo);
            var moduleType = assemblyDefinition.MainModule.GetType("<Module>");
            var moduleConstructor = FindOrCreateCctor(moduleType);
            var ilProcessor = moduleConstructor.Body.GetILProcessor();

            var initInstruction = ilProcessor.Create(OpCodes.Call, initMethodReference);
            if (moduleConstructor.Body.Instructions.Count > 0)
                ilProcessor.InsertBefore(moduleConstructor.Body.Instructions[0], initInstruction);
            else
                ilProcessor.Append(initInstruction);

            var pathParamLoadInstruction = ilProcessor.Create(OpCodes.Ldstr, hitsFile);
            ilProcessor.InsertBefore(initInstruction, pathParamLoadInstruction);
        }

        private bool HasSourceFiles(string assemblyFile)
        {
            using (var assemblyDefinition = AssemblyDefinition.ReadAssembly(assemblyFile, new ReaderParameters { ReadSymbols = true }))
            {
                foreach (var type in assemblyDefinition.MainModule.GetTypes())
                {
                    if (HasSourceFiles(type))
                        return true;
                }

                return false;
            }
        }

        private bool HasSourceFiles(TypeDefinition type)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                if (HasSourceFiles(method))
                    return true;
            }

            foreach (var subType in type.NestedTypes)
            {
                if (HasSourceFiles(subType))
                    return true;
            }

            return false;
        }

        private bool HasSourceFiles(MethodDefinition methodDefinition)
        {
            return methodDefinition.DebugInformation.SequencePoints
                .Select(s => s.Document.Url)
                .Any(d => GetSourceRelativePath(d) != null);
        }

        private bool IsInstrumented(string assemblyFile)
        {
            using (var assemblyDefinition = AssemblyDefinition.ReadAssembly(assemblyFile))
            {
                return assemblyDefinition.CustomAttributes.Any(a => a.AttributeType.Name == "InstrumentedAttribute");
            }
        }

        private void InstrumentType(TypeDefinition type, MethodReference hitMethodReference)
        {
            if (type.FullName == "<Module>" || type.FullName == "AutoGeneratedProgram")
                return;

            foreach (var subType in type.NestedTypes)
                InstrumentType(subType, hitMethodReference);

            foreach (var method in type.Methods.Where(m => m.HasBody))
                InstrumentMethod(method, hitMethodReference);
        }

        private void InstrumentMethod(MethodDefinition method, MethodReference hitMethodReference)
        {
            var ilProcessor = method.Body.GetILProcessor();

            ilProcessor.Body.SimplifyMacros();

            var instructions = method.Body.Instructions.ToDictionary(i => i.Offset);
            foreach (var fileGroup in method.DebugInformation.SequencePoints.GroupBy(s => s.Document.Url))
            {
                var sourceRelativePath = GetSourceRelativePath(fileGroup.Key);
                if (sourceRelativePath == null)
                    return;

                var fileLines = File.ReadAllLines(fileGroup.Key);

                foreach (var sequencePoint in fileGroup)
                {
                    var code = ExtractCode(fileLines, sequencePoint);
                    if (code == null || code == "{" || code == "}")
                        continue;

                    var instruction = instructions[sequencePoint.Offset];

                    // if the previous instruction is a Prefix instruction then this instruction MUST go with it.
                    // we cannot put an instruction between the two.
                    if (instruction.Previous != null && instruction.Previous.OpCode.OpCodeType == OpCodeType.Prefix)
                        return;

                    var instructionId = ++id;

                    result.AddInstruction(sourceRelativePath, new InstrumentedInstruction
                    {
                        Id = instructionId,
                        StartLine = sequencePoint.StartLine,
                        EndLine = sequencePoint.EndLine,
                        StartColumn = sequencePoint.StartColumn,
                        EndColumn = sequencePoint.EndColumn,
                        Instruction = instruction.ToString()
                    });

                    InstrumentInstruction(instructionId, instruction, hitMethodReference, method, ilProcessor);
                }
            }

            ilProcessor.Body.OptimizeMacros();
        }

        private void InstrumentInstruction(int instructionId, Instruction instruction,
            MethodReference hitMethodReference, MethodDefinition method, ILProcessor ilProcessor)
        {
            var pathParamLoadInstruction = ilProcessor.Create(OpCodes.Ldstr, hitsFile);
            var lineParamLoadInstruction = ilProcessor.Create(OpCodes.Ldc_I4, instructionId);
            var registerInstruction = ilProcessor.Create(OpCodes.Call, hitMethodReference);

            ilProcessor.InsertBefore(instruction, registerInstruction);
            ilProcessor.InsertBefore(registerInstruction, lineParamLoadInstruction);
            ilProcessor.InsertBefore(lineParamLoadInstruction, pathParamLoadInstruction);

            var newFirstInstruction = pathParamLoadInstruction;

            //change try/finally etc to point to our first instruction if they referenced the one we inserted before
            foreach (var handler in method.Body.ExceptionHandlers)
            {
                if (handler.FilterStart == instruction)
                    handler.FilterStart = newFirstInstruction;

                if (handler.TryStart == instruction)
                    handler.TryStart = newFirstInstruction;
                if (handler.TryEnd == instruction)
                    handler.TryEnd = newFirstInstruction;

                if (handler.HandlerStart == instruction)
                    handler.HandlerStart = newFirstInstruction;
                if (handler.HandlerEnd == instruction)
                    handler.HandlerEnd = newFirstInstruction;
            }

            //change instructions with a target instruction if they referenced the one we inserted before to be our first instruction
            foreach (var iteratedInstruction in method.Body.Instructions)
            {
                var operand = iteratedInstruction.Operand;
                if (operand == instruction)
                {
                    iteratedInstruction.Operand = newFirstInstruction;
                    continue;
                }

                if (!(operand is Instruction[]))
                    continue;

                var operands = (Instruction[])operand;
                for (var i = 0; i < operands.Length; ++i)
                {
                    if (operands[i] == instruction)
                        operands[i] = newFirstInstruction;
                }
            }
        }

        private string GetSourceRelativePath(string path)
        {
            if (!path.StartsWith(normalizedWorkDir))
                return null;

            if (!sourceFiles.Contains(path))
                return null;

            return path.Substring(normalizedWorkDir.Length);
        }

        private string ExtractCode(string[] fileLines, SequencePoint sequencePoint)
        {
            if (sequencePoint.StartLine > fileLines.Length)
                return null;

            if (sequencePoint.StartLine == sequencePoint.EndLine)
            {
                return fileLines[sequencePoint.StartLine - 1].Substring(sequencePoint.StartColumn - 1, sequencePoint.EndColumn - sequencePoint.StartColumn);
            }
            else
            {
                var result = new List<string>();
                result.Add(fileLines[sequencePoint.StartLine - 1].Substring(sequencePoint.StartColumn - 1));
                for (var l = sequencePoint.StartLine; l <= sequencePoint.EndLine - 2; l++)
                {
                    result.Add(fileLines[l]);
                }
                result.Add(fileLines[sequencePoint.EndLine - 1].Substring(0, sequencePoint.EndColumn - 1));
                return string.Join(Environment.NewLine, result);
            }
        }

        private static MethodDefinition FindOrCreateCctor(TypeDefinition typeDefinition)
        {
            var cctor = typeDefinition.Methods.FirstOrDefault(x => x.Name == ".cctor");
            if (cctor == null)
            {
                var attributes = Mono.Cecil.MethodAttributes.Static
                                 | Mono.Cecil.MethodAttributes.SpecialName
                                 | Mono.Cecil.MethodAttributes.RTSpecialName;
                cctor = new MethodDefinition(".cctor", attributes, typeDefinition.Module.TypeSystem.Void);
                typeDefinition.Methods.Add(cctor);
                cctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            return cctor;
        }
    }
}