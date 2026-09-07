using System;
using System.IO;
using HybridCLR.Editor.Il2CppDef;
using HybridCLR.Editor.Installer;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{
    internal static class DheAotCompiler
    {
        private static readonly object Sync = new object();
        private static DheAotCompilerSession session;
        private static int ownerThread;
        private static int depth;
        private static string previousIl2CppPath;

        public static Scope Enter()
        {
            lock (Sync)
            {
                int thread = Environment.CurrentManagedThreadId;
                if (session != null)
                {
                    if (ownerThread != thread) throw new BuildFailedException("A DHE compiler build is already active on another thread.");
                    session.Validate();
                }
                else
                {
                    if (!SettingsUtil.Enable || SettingsUtil.HybridCLRSettings.useGlobalIl2cpp)
                        throw new BuildFailedException("DHE compiler preparation requires a project-local HybridCLR installation.");
                    string local = Path.Combine(SettingsUtil.LocalIl2CppDir, "build", "deploy", "Unity.IL2CPP.dll");
                    string originalRoot = new InstallerController().ApplicationIl2cppPath;
                    string original = Path.Combine(originalRoot, "build", "deploy", "Unity.IL2CPP.dll");
                    if (!File.Exists(original) || !File.Exists(local))
                        throw new BuildFailedException("Unsupported DHE compiler layout. Expected build/deploy/Unity.IL2CPP.dll in the Editor and local installation.");
                    session = new DheAotCompilerSession(SettingsUtil.ProjectDir, original, local,
                        Application.unityVersion, PlayerSettings.GetAdditionalIl2CppArgs, PlayerSettings.SetAdditionalIl2CppArgs);
                    previousIl2CppPath = Environment.GetEnvironmentVariable("UNITY_IL2CPP_PATH");
                    Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH", SettingsUtil.LocalIl2CppDir);
                    ownerThread = thread;
                    Debug.Log("[HybridCLR DHE] AOT compiler identity: " + JsonUtility.ToJson(session.Identity));
                }
                depth++;
                return new Scope(session);
            }
        }

        internal sealed class Scope : IDisposable
        {
            private readonly DheAotCompilerSession current;
            private bool disposed;
            public DheAotCompilerIdentity Identity => current.Identity;
            internal Scope(DheAotCompilerSession current) { this.current = current; }
            public void RecordGeneration(string root)
            {
                string actual = Environment.GetEnvironmentVariable("UNITY_IL2CPP_PATH");
                if (string.IsNullOrEmpty(actual) || Path.GetFullPath(actual) != Path.GetFullPath(SettingsUtil.LocalIl2CppDir))
                    throw new BuildFailedException("DHE generation did not use the project-local compiler path.");
                current.RecordGeneration(root);
            }
            public void RequireGeneration(string root) => current.RequireGeneration(root);
            public void Dispose()
            {
                lock (Sync)
                {
                    if (disposed) return;
                    if (ownerThread != Environment.CurrentManagedThreadId)
                        throw new BuildFailedException("DHE compiler scope must be disposed on its owning thread.");
                    disposed = true;
                    if (--depth == 0)
                    {
                        try { current.Dispose(); }
                        finally
                        {
                            Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH", previousIl2CppPath);
                            session = null;
                            ownerThread = 0;
                        }
                    }
                }
            }
        }
    }
}
