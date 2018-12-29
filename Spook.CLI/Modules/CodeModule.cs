﻿using System;
using System.Collections.Generic;
using System.IO;
using Phantasma.Numerics;
using Phantasma.AssemblerLib;
using Phantasma.CodeGen.Core;
using Phantasma.VM.Utils;

namespace Phantasma.Spook.Modules
{
    public static class CodeModule
    {
        public static void AssembleFile(string[] args)
        {
            string sourceFilePath = null;

            try
            {
                sourceFilePath = args[0];
            }
            catch
            {
                throw new CommandException("Could not obtain input filename");
            }

            string[] lines = null;
            try
            {
                lines = File.ReadAllLines(sourceFilePath);
            }
            catch
            {
                throw new CommandException("Error reading " + sourceFilePath);
            }

            IEnumerable<Semanteme> semantemes = null;
            try
            {
                semantemes = Semanteme.ProcessLines(lines);
            }
            catch (Exception e)
            {
                throw new CommandException("Error parsing " + sourceFilePath + " :" + e.Message);
            }

            var sb = new ScriptBuilder();
            byte[] script = null;

            try
            {
                foreach (var entry in semantemes)
                {
                    Console.WriteLine($"{entry}");
                    entry.Process(sb);
                }
                script = sb.ToScript();
            }
            catch (Exception e)
            {
                throw new CommandException("Error assembling " + sourceFilePath + " :" + e.Message);
            }

            var extension = Path.GetExtension(sourceFilePath);
            var outputName = sourceFilePath.Replace(extension, Format.Extension);

            try
            {
                File.WriteAllBytes(outputName, script);
            }
            catch
            {
                throw new CommandException("Error generating " + outputName);
            }
        }

        public static void CompileFile(string[] args)
        {
            string sourceFilePath = null;

            try
            {
                sourceFilePath = args[0];
            }
            catch
            {
                throw new CommandException("Could not obtain input filename");
            }

            var extension = Path.GetExtension(sourceFilePath);

            var src = File.ReadAllText(sourceFilePath);

            var language = LanguageProcessor.GetLanguage(extension);
            if (language == Language.Unknown)
            {
                throw new CommandException("Unsupported smart contract language extension: "+extension);
            }

            var processor = LanguageProcessor.GetProcessor(language);

            var tokens = processor.Lexer.Execute(src);
            var tree = processor.Parser.Execute(tokens);
            var compiler = new Compiler();
            var instructions = compiler.Execute(tree);
            var generator = new ByteCodeGenerator(tree, instructions);

            var outputName = sourceFilePath.Replace(extension, Format.Extension);

            try
            {
                File.WriteAllBytes(outputName, generator.Script);
            }
            catch
            {
                throw new CommandException("Error generating " + outputName);
            }
        }
    }
}
