using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;

namespace HybridCLR.Editor.Commands
{
    /// <summary>
    /// Installs a preassembled DHE libil2cpp tree into this Unity project.
    /// The command is intentionally package-owned so project adapters only
    /// provide the runtime source path and do not duplicate Installer logic.
    /// </summary>
    public static class DheRuntimeCommand
    {
        public static void InstallRuntime()
        {
            string source = GetRequiredArgument("-dheRuntimeSource");
            string resolvedSource = Path.GetFullPath(source);
            RequireDirectory(resolvedSource, "DHE runtime source");
            RequireFile(Path.Combine(resolvedSource, "hybridclr", "DheRuntime.cpp"),
                "DHE runtime implementation");
            RequireFile(Path.Combine(resolvedSource, "hybridclr", "DheRuntime.h"),
                "DHE runtime header");

            var installer = new Installer.InstallerController();
            installer.InstallFromLocal(resolvedSource);
            if (!installer.HasInstalledHybridCLR())
            {
                throw new BuildFailedException("HybridCLR runtime installation did not produce LocalIl2CppData.");
            }

            string installedRoot = Path.GetFullPath(SettingsUtil.LocalIl2CppDir + "/libil2cpp");
            VerifyTree(resolvedSource, installedRoot);
            UnityEngine.Debug.Log("DHE runtime installed and verified: " + installedRoot);
        }

        private static string GetRequiredArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length - 1; index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[index + 1];
                }
            }
            throw new BuildFailedException("Missing required Unity argument: " + name);
        }

        private static void RequireDirectory(string path, string description)
        {
            if (!Directory.Exists(path))
            {
                throw new BuildFailedException(description + " was not found: " + path);
            }
        }

        private static void RequireFile(string path, string description)
        {
            if (!File.Exists(path))
            {
                throw new BuildFailedException(description + " was not found: " + path);
            }
        }

        private static void VerifyTree(string sourceRoot, string installedRoot)
        {
            foreach (string sourcePath in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relative = sourcePath.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string installedPath = Path.Combine(installedRoot, relative);
                RequireFile(installedPath, "Installed DHE runtime file '" + relative + "'");
                if (!StringComparer.OrdinalIgnoreCase.Equals(Hash(sourcePath), Hash(installedPath)))
                {
                    throw new BuildFailedException("Installed DHE runtime file differs from source: " + relative);
                }
            }
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }
}
