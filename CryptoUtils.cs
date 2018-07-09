﻿using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CharacterBuilderLoader
{
    /// <summary>
    /// A class that handles calculating the hashes the character builder uses
    /// to validate its files.
    /// 
    /// This supports calculating such checksums, and performing preimage attacks
    /// to fix the hash of our generated files. (Since the hash is hardcoded into
    /// D20RulesEngine.dll)
    /// </summary>
    internal sealed class CBDataHasher
    {
        public uint State { get; set; }

        public CBDataHasher()
        {
            this.State = 5381;
        }
        public CBDataHasher(uint state)
        {
            this.State = state;
        }

        public void Update(char ch)
        {
            this.State *= 33;
            this.State += ch;
        }
        public void Update(string str)
        {
            foreach (var ch in str) Update(ch);
        }

        private void UpdateReverse(char ch)
        {
            this.State -= ch;
            this.State *= 1041204193; // 33^-1 mod 2^32
        }
        private void UpdateReverse(string str)
        {
            foreach (var ch in str.Reverse()) UpdateReverse(ch);
        }
        
        public string CalculatePreimage(uint targetHash, string tail)
        {
            CBDataHasher target = new CBDataHasher(targetHash);
            target.UpdateReverse(tail);
            target.State -= this.State * 39135393; // 33^5

            var outStr = "";
            for (int i = 0; i < 5; i++)
            {
                var tint = target.State;
                var next = (tint >= 0xD800 && tint <= 0xDFFF) || tint == 0xFEFF ? 0x752D + (tint % 33) :
                           tint <= 0xFFFF ? tint : 0xFFC0 + (tint % 33);
                var next_ch = (char)next;

                outStr = next_ch + outStr;
                target.UpdateReverse(next_ch);
            }
            Trace.Assert(target.State == 0, "CalculatePreimage failed.");
            return outStr + tail;
        }
    }

    internal sealed class ParsedD20RulesEngine
    {
        public readonly uint expectedDemoHash, expectedNormalHash;

        public ParsedD20RulesEngine(string assemblyPath)
        {
            Log.Debug(" - Parsing D20RulesEngine.dll");

            var assembly = AssemblyDef.Load(assemblyPath);
            var moduleBase = assembly.ManifestModule.Find("<Module>", false);
            var method = moduleBase.FindMethod("D20RulesEngine.LoadRulesFile");

            method.Body.SimplifyMacros(method.Parameters);

            // Find call to ComputeHash
            int computeHashStart = -1;
            for (int i = 0; i < method.Body.Instructions.Count; i++)
            {
                if (method.Body.Instructions[i].OpCode == OpCodes.Call &&
                    ((IMethod) method.Body.Instructions[i].Operand).Name == "GenerateHash")
                {
                    computeHashStart = i;
                    break;
                }
            }
            if (computeHashStart == -1) throw new Exception("Cannot find invocation of ComputeHash in LoadRulesFile.");

            // Find the local the hash is stored in.
            if (method.Body.Instructions[computeHashStart + 1].OpCode != OpCodes.Stloc)
                throw new Exception("stloc does not follow call to ComputeHash in LoadRulesFile.");
            var hashVar = (IVariable) method.Body.Instructions[computeHashStart + 1].Operand;

            // Find the three comparisons that should follow.
            var hashes = new uint[3];
            int foundComparisons = 0;
            for (int i = computeHashStart; i < Math.Min(method.Body.Instructions.Count - 2, computeHashStart + 30); i++)
            {
                if (method.Body.Instructions[i + 0].OpCode == OpCodes.Ldloc &&
                    ((IVariable) method.Body.Instructions[i + 0].Operand).Index == hashVar.Index &&
                    method.Body.Instructions[i + 1].OpCode == OpCodes.Ldc_I4 &&
                    method.Body.Instructions[i + 2].OpCode.OperandType == OperandType.InlineBrTarget)
                {
                    var currentHash = (uint) (int) method.Body.Instructions[i + 1].Operand;
                    hashes[foundComparisons++] = currentHash;
                    if (foundComparisons == 3) break;
                }
            }
            if (foundComparisons != 3) throw new Exception("Not enough comparisons found in LoadRulesFile.");
            if (hashes[0] != hashes[2]) throw new Exception("hashes[0] != hashes[2] in LoadRulesFile.");

            // Output hash information.
            Log.Trace("   - demoHash = " + hashes[0] + ", normalHash = " + hashes[1]);
            this.expectedDemoHash = hashes[0];
            this.expectedNormalHash = hashes[1];
        }
    }

    internal sealed class ParsedKeyFile
    {
        public readonly Guid currentUpdateGuid;
        public readonly byte[] keyData;

        public ParsedKeyFile(Guid applicationId, string keyFile)
        {
            Log.Debug(" - Parsing " + Path.GetFileName(keyFile));
            var document = XDocument.Load(keyFile);

            var applicationTag = document.Root.Elements()
                .First(x => x.Attribute("ID").Value == applicationId.ToString());
            var currentUpdateGuid = new Guid(applicationTag.Attribute("CurrentUpdate").Value);
            var updateTag = applicationTag.Element("Update" + currentUpdateGuid);
            var updateKey = Convert.FromBase64String(updateTag.Value.ToString());

            this.currentUpdateGuid = currentUpdateGuid;
            this.keyData = updateKey;
        }
    }

    public sealed class CryptoInfo
    {
        public readonly uint expectedDemoHash, expectedNormalHash;

        public readonly Guid demoUpdateGuid;
        public readonly byte[] demoKeyData;

        public readonly byte[] regPatcherKeyData;

        public CryptoInfo(string baseDirectory)
        {
            Log.Info("Loading encryption keys.");
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var parsedD20Engine = new ParsedD20RulesEngine(Path.Combine(baseDirectory, "D20RulesEngine.dll"));
            var parsedKeyFile = new ParsedKeyFile(CryptoUtils.CB_APP_ID, Path.Combine(baseDirectory, "HeroicDemo.update"));

            this.expectedDemoHash = parsedD20Engine.expectedDemoHash;
            this.expectedNormalHash = parsedD20Engine.expectedNormalHash;
            this.demoUpdateGuid = parsedKeyFile.currentUpdateGuid;
            this.demoKeyData = parsedKeyFile.keyData;

            var regPatcherPath = Path.Combine(baseDirectory, "RegPatcher.dat");
            this.regPatcherKeyData = File.Exists(regPatcherPath) ? Convert.FromBase64String(File.ReadAllText(regPatcherPath)) : null;

            stopwatch.Stop();
            Log.Debug("Finished in " + stopwatch.ElapsedMilliseconds + " ms");
            Log.Debug("");
        }

        private string FixXmlHash(string str) =>
            CryptoUtils.FixXmlHash(str, this.expectedDemoHash);
        
        private Stream GetXmlEncryptingStream(Stream s) =>
            CryptoUtils.GetEncryptingStream(s, CryptoUtils.CB_APP_ID, this.demoUpdateGuid, this.demoKeyData);
        
        public void SaveRulesFile(XDocument document, string filename)
        {
            using (var crypt = GetXmlEncryptingStream(File.Open(filename, FileMode.Create)))
            {
                var bytes = Encoding.UTF8.GetBytes(FixXmlHash(document.ToString()));
                crypt.Write(bytes, 0, bytes.Length);
            }
        }

        private byte[] KeyForGuid(Guid updateId)
        {
            if (updateId == demoUpdateGuid) return demoKeyData;

            if (regPatcherKeyData == null)
            {
                Log.Error("Could not retrieve key data from RegPatcher.dat.\nPlease update the Character Builder to the April 2009 patch or later.");
            }

            return regPatcherKeyData;
        }

        public Stream OpenEncryptedFile(string filename) =>
            CryptoUtils.GetDecryptingStream(File.Open(filename, FileMode.Open), CryptoUtils.CB_APP_ID, KeyForGuid);
    }

    public sealed class CryptoUtils
    {
        public static Guid CB_APP_ID = new Guid("2a1ddbc4-4503-4392-9548-d0010d1ba9b1");
        public static Guid HEROIC_DEMO_UPDATE_ID = new Guid("19806aaa-6d71-425d-9dcf-54e6bb6b1e57");
        
        private static string XML_MARKER = "<!-- This file has been edited by CBLoader -->";

        public static bool IsXmlPatched(string str)
        {
            return str.Contains(XML_MARKER);
        }
        public static uint HashString(string str)
        {
            var hasher = new CBDataHasher();
            hasher.Update(str);
            return hasher.State;
        }
        public static string FixXmlHash(string str, uint target_hash)
        {
            if (str[0] == '\uFEFF')
            {
                // This is apparently stripped by ReadToEnd.
                Log.Debug("Stripping UTF-8 BOM.");
                str = str.Substring(1, str.Length - 1);
            }
            str = str.Replace("\r\n", "\n").Replace('\r', '\n');

            str += "\n" + XML_MARKER + "\n<!-- Fix hash: ";
            var hasher = new CBDataHasher();
            hasher.Update(str);
            str += hasher.CalculatePreimage(target_hash, " -->\n");
            Trace.Assert(HashString(str) == target_hash, "FixXmlHash failed!");
            return str;
        }

        private static SymmetricAlgorithm CreateAlgorithm(Guid applicationId, byte[] keyData)
        {
            var algorithm = new AesCryptoServiceProvider();
            algorithm.IV = applicationId.ToByteArray().Take(algorithm.BlockSize / 8).ToArray();
            algorithm.Key = keyData;
            return algorithm;
        }
        public static Stream GetDecryptingStream(Stream inStream, Guid applicationId, Func<Guid, byte[]> getKeyData)
        {
            var guidData = new byte[16];
            Trace.Assert(inStream.Read(guidData, 0, 16) == 16, "Not enough bytes read from GUID.");
            var updateId = new Guid(guidData);
            var keyData = getKeyData.Invoke(updateId);

            var algorithm = CreateAlgorithm(applicationId, keyData);
            var cryptoStream = new CryptoStream(inStream, algorithm.CreateDecryptor(), CryptoStreamMode.Read);
            return new GZipStream(cryptoStream, CompressionMode.Decompress);
        }
        public static Stream GetEncryptingStream(Stream outStream, Guid applicationId, Guid updateId, byte[] keyData)
        {
            outStream.Write(updateId.ToByteArray(), 0, 16);

            var algorithm = CreateAlgorithm(applicationId, keyData);
            var cryptoStream = new CryptoStream(outStream, algorithm.CreateEncryptor(), CryptoStreamMode.Write);
            return new GZipStream(cryptoStream, CompressionMode.Compress);
        }
    }
}