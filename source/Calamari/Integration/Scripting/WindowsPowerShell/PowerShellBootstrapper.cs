﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using Calamari.Deployment;
using Calamari.Integration.Processes;
using Octostache;

namespace Calamari.Integration.Scripting.WindowsPowerShell
{
    public class PowerShellBootstrapper
    {
        static string powerShellPath;
        const string EnvPowerShellPath = "PowerShell.exe";
        private static readonly string BootstrapScriptTemplate;

        static PowerShellBootstrapper()
        {
            BootstrapScriptTemplate = EmbeddedResource.ReadEmbeddedText(typeof (PowerShellBootstrapper).Namespace + ".Bootstrap.ps1");
        }

        public static string PathToPowerShellExecutable()
        {
            if (powerShellPath != null)
            {
                return powerShellPath;
            }

            try
            {
                var systemFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
                powerShellPath = Path.Combine(systemFolder, @"WindowsPowershell\v1.0\", EnvPowerShellPath);

                if (!File.Exists(powerShellPath))
                {
                    powerShellPath = EnvPowerShellPath;
                }
            }
            catch (Exception)
            {
                powerShellPath = EnvPowerShellPath;
            }

            return powerShellPath;
        }

        public static string FormatCommandArguments(string bootstrapFile)
        {
            var commandArguments = new StringBuilder();
            commandArguments.Append("-NoLogo ");
            commandArguments.Append("-ExecutionPolicy Unrestricted ");
            var escapedBootstrapFile = bootstrapFile.Replace("'", "''");
            commandArguments.AppendFormat("-Command \". {{. '{0}'; if ((test-path variable:global:lastexitcode)) {{ exit $LastExitCode }}}}\"", escapedBootstrapFile);
            return commandArguments.ToString();
        }

        public static string PrepareBootstrapFile(string targetScriptFile, VariableDictionary variables)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(targetScriptFile));
            var name = Path.GetFileName(targetScriptFile);
            var bootstrapFile = Path.Combine(parent, "Bootstrap." + name);

            var builder = new StringBuilder(BootstrapScriptTemplate);
            builder.Replace("{{TargetScriptFile}}", targetScriptFile);
            builder.Replace("{{VariableDeclarations}}", DeclareVariables(variables));

            using (var writer = new StreamWriter(bootstrapFile))
            {
                writer.WriteLine(builder.ToString());
                writer.Flush();
            }

            File.SetAttributes(bootstrapFile, FileAttributes.Hidden);
            return bootstrapFile;
        }

        private static string DeclareVariables(VariableDictionary variables)
        {
            var output = new StringBuilder();

            WriteScriptModules(variables, output);
            output.AppendLine();
            WriteVariableDictionary(variables, output);
            output.AppendLine();
            WriteLocalVariables(variables, output);

            return output.ToString();
        }

        static void WriteScriptModules(VariableDictionary variables, StringBuilder output)
        {
            foreach (var variableName in variables.GetNames().Where(SpecialVariables.IsLibraryScriptModule))
            {
                var name = "Library_" + new string(SpecialVariables.GetLibraryScriptModuleName(variableName).Where(char.IsLetterOrDigit).ToArray()) + "_" + DateTime.Now.Ticks;
                output.Append("New-Module -Name ").Append(name).Append(" -ScriptBlock {");
                output.AppendLine(variables.Get(variableName));
                output.AppendLine("} | Import-Module");
                output.AppendLine();
            }
        }

        static void WriteVariableDictionary(VariableDictionary variables, StringBuilder output)
        {
            output.AppendLine("$OctopusParameters = New-Object 'System.Collections.Generic.Dictionary[String,String]' (,[System.StringComparer]::OrdinalIgnoreCase)");
            foreach (var variableName in variables.GetNames().Where(name => !SpecialVariables.IsLibraryScriptModule(name)))
            {
                output.Append("$OctopusParameters[").Append(EncodeValue(variableName)).Append("] = ").AppendLine(EncodeValue(variables.Get(variableName)));
            }
        }

        static void WriteLocalVariables(VariableDictionary variables, StringBuilder output)
        {
            foreach (var variableName in variables.GetNames().Where(name => !SpecialVariables.IsLibraryScriptModule(name)))
            {
                if (SpecialVariables.IsExcludedFromLocalVariables(variableName))
                {
                    continue;
                }

                // This is the way we used to fix up the identifiers - people might still rely on this behavior
                var legacyKey = new string(variableName.Where(char.IsLetterOrDigit).ToArray());

                // This is the way we should have done it
                var smartKey = new string(variableName.Where(IsValidPowerShellIdentifierChar).ToArray());

                if (legacyKey != smartKey)
                {
                    WriteVariableAssignment(output, legacyKey, variableName);
                }

                WriteVariableAssignment(output, smartKey, variableName);
            }
        }

        static void WriteVariableAssignment(StringBuilder writer, string key, string variableName)
        {
            writer.Append("if (-Not (test-path variable:global:").Append(key).AppendLine(")) {");
            writer.Append("  $").Append(key).Append(" = $OctopusParameters[").Append(EncodeValue(variableName)).AppendLine("]");
            writer.AppendLine("}");
        }

        static string EncodeValue(string value)
        {
            if (value == null)
                return "$null";

            var bytes = Encoding.UTF8.GetBytes(value);

            return string.Format("[System.Text.Encoding]::UTF8.GetString(" + "[Convert]::FromBase64String(\"{0}\")" + ")", Convert.ToBase64String(bytes));
        }

        static bool IsValidPowerShellIdentifierChar(char c)
        {
            return c == '_' || char.IsLetterOrDigit(c);
        }
    }
}
