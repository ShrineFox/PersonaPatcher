﻿using ShrineFox.IO;
using PersonaPatchGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibOrbisPkg;
using LibOrbisPkg.PKG;
using System.IO.MemoryMappedFiles;
using LibOrbisPkg.Util;
using LibOrbisPkg.PFS;
using PersonaGameLib;

namespace PersonaPatchGen
{
    public partial class WizardForm
    {
        private void PatchPS4Game()
        {
            SetProgress(0);

            // Proceed if Python 3 is installed
            if (Python3Installed())
            {
                SetProgress(1);
                PatchLog($"Beginning patching process, please wait...");

                // Create temp directory for extracted files if one doesn't exist
                string tempDir = Path.Combine(Exe.Directory(), $"Temp\\{selectedGame.TitleID}");
                if (!Directory.Exists($"{tempDir}\\base_extracted"))
                    Directory.CreateDirectory($"{tempDir}\\base_extracted");

                // Extract required files from PKG to temp directory
                if (!File.Exists($"{tempDir}\\base_extracted\\Sc0\\nptitle.dat") || !File.Exists($"{tempDir}\\base_extracted\\Sc0\\npbind.dat")
                    || !File.Exists($"{tempDir}\\base_extracted\\Image0\\eboot.bin"))
                    ExtractPKG(tempDir + "\\base_extracted");

                // Get list of combination sets of patches to generate
                List<GamePatch> selectedPatches = new List<GamePatch>();
                foreach (var patch in chkListBox_Patches.CheckedItems)
                    selectedPatches.Add(selectedGame.Patches.First(x => x.Name.Equals(patch.ToString())));

                //List<List<GamePatch>> patchCombos = GetPatchCombos();
                List<List<GamePatch>> patchCombos = new() { selectedPatches };

                // Create new PKG for each set of combinations
                for (int i = 0; i < patchCombos.Count; i++)
                {
                    PatchLog($"Creating Fake Update PKG ({i + 1}/{patchCombos.Count()})");
                    Thread.Sleep(200);
                    CreateUpdatePKG(patchCombos[i], tempDir);
                }
                
            }
            else
            {
                if (WinFormsDialogs.ShowMessageBox("Install Python 3?", "An installation of Python 3.0.0 or higher was not detected on the system. " +
                    "It is required to complete the patching operation. Would you like to download and install it now?", MessageBoxButtons.YesNo))
                    DownloadPython3();
            }
        }

        private bool CheckP5RUpdateFiles()
        {
            bool fileMissing = false;
            string updateFilesDir = Path.Combine(Exe.Directory(), $"Temp\\{selectedGame.TitleID}\\{selectedGame.TitleID}-patch\\USRDIR");

            foreach (var file in new string[] { "patch2R.cpk", "patch2R_F.cpk", "patch2R_G.cpk", "patch2R_I.cpk", "patch2R_S.cpk", "eboot.bin" })
            {
                if (!File.Exists(Path.Combine(updateFilesDir, file)))
                    fileMissing = true;
            }
            if (fileMissing)
            {
                MessageBox.Show($"Please place the extracted CPKs (and unfakesigned eboot.bin) from the P5R v1.02 update in the following directory and try again: {updateFilesDir}");
                return false;
            }

            return true;
        }

        private void ExtractPKG(string tempDir)
        {
            PatchLog("Extracting required files from Base Game PKG...");
            
            var pkgPath = txt_GamePath.Text;
            var passcode = "00000000000000000000000000000000";

            // Extract required files from PKG Meta
            ExtractEntries(pkgPath, Path.Combine(tempDir, "Sc0"), passcode, new List<string>() { "NPBIND_DAT", "NPTITLE_DAT" });

            // Extract EBOOT.BIN from PKG inner image
            Pkg pkg;
            var mmf = MemoryMappedFile.CreateFromFile(pkgPath);
            using (var s = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read))
            {
                pkg = new PkgReader(s).ReadPkg();
            }

            var ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
            var outerPfsOffset = (long)pkg.Header.pfs_image_offset;
            using (var acc = mmf.CreateViewAccessor(outerPfsOffset, (long)pkg.Header.pfs_image_size, MemoryMappedFileAccess.Read))
            {
                var outerPfs = new PfsReader(acc, pkg.Header.pfs_flags, ekpfs);
                var inner = new PfsReader(new PFSCReader(outerPfs.GetFile("pfs_image.dat").GetView()));
                ExtractInParallel(inner, tempDir, new List<string>() { "eboot.bin" });
            }

            PatchLog("Successfully extracted Base Game files!");
        }

        private void ExtractEntries(string pkgPath, string outPath, string passcode, List<string> list)
        {
            using (var file = File.OpenRead(pkgPath))
            {
                var pkg = new PkgReader(file).ReadPkg();
                foreach (var meta in pkg.Metas.Metas.Where(x => list.Any(y => y.Equals(x.id.ToString()))))
                    ExtractEntry(pkg, file, meta, passcode, Path.Combine(outPath, meta.id.ToString().Replace("_",".").ToLower()));
            }
        }

        private void ExtractEntry(Pkg pkg, FileStream pkgFile, MetaEntry meta, string passcode, string outPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            using (var outFile = File.Create(outPath))
            {
                outFile.SetLength(meta.DataSize);
                var totalEntrySize = meta.Encrypted ? (meta.DataSize + 15) & ~15 : meta.DataSize;
                if (meta.Encrypted)
                {
                    var entry = new SubStream(pkgFile, meta.DataOffset, totalEntrySize);
                    var tmp = new byte[totalEntrySize];
                    entry.Read(tmp, 0, tmp.Length);
                    tmp = meta.KeyIndex == 3 ? Entry.Decrypt(tmp, pkg, meta) : Entry.Decrypt(tmp, pkg.Header.content_id, passcode, meta);
                    outFile.Write(tmp, 0, (int)meta.DataSize);
                }
                //new SubStream(pkgFile, meta.DataOffset, totalEntrySize).CopyTo(outFile);
            }
            
        }

        private static void ExtractInParallel(PfsReader inner, string outPath, List<string> fileList)
        {
            Parallel.ForEach(
              inner.GetAllFiles().Where(x => fileList.Any(y => x.FullName.Contains(y))),
              () => new byte[0x10000],
              (f, _, buf) =>
              {
                  var size = f.size;
                  long pos = 0;
                  var view = f.GetView();
                  var fullName = f.FullName;
                  var path = Path.Combine(outPath, fullName.Replace('/', Path.DirectorySeparatorChar).Substring(1)).Replace("uroot", "Image0");
                  var dir = path.Substring(0, path.LastIndexOf(Path.DirectorySeparatorChar));
                  Directory.CreateDirectory(dir);
                  using (var file = File.OpenWrite(path))
                  {
                      file.SetLength(size);
                      while (size > 0)
                      {
                          var toRead = (int)Math.Min(size, buf.Length);
                          view.Read(pos, buf, 0, toRead);
                          file.Write(buf, 0, toRead);
                          pos += toRead;
                          size -= toRead;
                      }
                  }
                  return buf;
              },
              x => { });
        }

        private void CreateUpdatePKG(List<GamePatch> patchCombo, string tempDir)
        {
            List<string> patchNames = new List<string>();
            List<string> patchShortNames = new List<string>();
            foreach (var patch in patchCombo)
            {
                patchNames.Add(patch.Name);
                patchShortNames.Add(patch.ShortName);
            }

            PatchLog($"Using patches: {String.Join(", ", patchNames)}");

            SetProgress(10);

            // Update path in gp4 to PKG
            string gp4Path = Path.Combine(Exe.Directory(), $"Dependencies\\PS4\\GenGP4\\{selectedGame.TitleID}-patch.gp4");
            var gp4Text = File.ReadAllLines(gp4Path);
            for (int i = 0; i < gp4Text.Count(); i++)
                if (gp4Text[i].Contains("app_path="))
                    gp4Text[i] = $"      app_path=\"{txt_GamePath.Text}\"";
            File.WriteAllText(gp4Path, string.Join("\n", gp4Text));

            SetProgress(20);

            // Create working dir from dependencies folder (updated with extracted files from base game PKG)
            string genGP4Dir = Path.Combine(Exe.Directory(), $"Dependencies\\PS4\\GenGP4\\{selectedGame.TitleID}-patch");
            string patchDir = Path.Combine(tempDir, $"{selectedGame.TitleID}-patch");
            FileSys.CopyDir(genGP4Dir, patchDir);

            if (File.Exists($"{tempDir}\\base_extracted\\Sc0\\nptitle.dat"))
                File.Copy($"{tempDir}\\base_extracted\\Sc0\\nptitle.dat", Path.Combine(patchDir, "sce_sys\\nptitle.dat"), true);
            if (File.Exists($"{tempDir}\\base_extracted\\Sc0\\npbind.dat"))
                File.Copy($"{tempDir}\\base_extracted\\Sc0\\npbind.dat", Path.Combine(patchDir, "sce_sys\\npbind.dat"), true);
            if (File.Exists($"{tempDir}\\base_extracted\\Image0\\eboot.bin"))
                File.Copy($"{tempDir}\\base_extracted\\Image0\\eboot.bin", Path.Combine(patchDir, "eboot.bin"), true);

            // Overwrite eboot with v1.02 update in patch folder before patching (if P5R)
            string updateFilesDir = Path.Combine(Exe.Directory(), $"Temp\\{selectedGame.TitleID}\\{selectedGame.TitleID}-patch\\USRDIR");
            if (selectedGame.ShortName == "P5R")
            {
                if (CheckP5RUpdateFiles())
                    File.Copy(Path.Combine(updateFilesDir, "eboot.bin"), Path.Combine(patchDir, "eboot.bin"), true);
                else
                {
                    SetProgress(100);
                    return;
                }
            }

            string tempGp4Path = Path.Combine(Path.GetDirectoryName(patchDir), $"{selectedGame.TitleID}-patch.gp4");
            File.Copy(gp4Path, tempGp4Path, true);

            SetProgress(30);

            // Patch EBOOT
            if (PatchEBOOTWithPPP(Path.Combine(patchDir, "eboot.bin"), patchShortNames))
            {
                PatchLog($"Patched EBOOT successfully.");

                SetProgress(50);

                // Update PKG description
                File.WriteAllText($"{genGP4Dir}\\sce_sys\\changeinfo\\changeinfo.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<changeinfo>\n  <changes app_ver=\"01.01\">\n    <![CDATA[\nPatched using ShrineFox's PersonaPatcher2 Software: https://shrinefox.com\n(based on zarroboogs's ppp):\n\n - {string.Join("\n - ", patchNames)}\n    ]]>\n  </changes>\n</changeinfo>");
                PatchLog("Edited Update PKG description.");

                SetProgress(70);

                PatchLog("Building PKG. Please be patient as this can take a very long time...");
                if (BuildPKG(patchShortNames, tempGp4Path))
                    PatchLog("Update PKG created successfully!");
                else
                    PatchLog("Update PKG creation failed.");
            }
            else
                PatchLog("Patched EBOOT creation failed.");
        }

        private bool BuildPKG(List<string> patchShortNames, string tempGp4Path)
        {
            // Create temp dir
            string tempPKGDir = Path.Combine(Path.GetDirectoryName(tempGp4Path), "temp");
            string buildDir = Path.Combine(Path.GetDirectoryName(tempGp4Path), $"{selectedGame.TitleID}-patch");
            Directory.CreateDirectory(tempPKGDir);

            // Generate new PKG file
            Exe.CloseProcess("orbis-pub-cmd", true);
            string args = $"img_create --oformat pkg --tmp_path ./temp ./{selectedGame.TitleID}-patch.gp4 ./temp";

            Exe.Run(Path.Combine(Exe.Directory(), 
                $"Dependencies\\PS4\\orbis-pub-cmd.exe"), args, true, 
                Path.GetDirectoryName(buildDir), 
                redirectStdOut: true, unicodeEncoding: false);
            
            // Move PKG/EBOOT to Output folder
            string pkgPath = Path.Combine(tempPKGDir, selectedGame.UpdatePKGName);
            using (FileSys.WaitForFile(pkgPath)) { };
            if (File.Exists(pkgPath))
            {
                string outputDir = $"{txt_OutputDir.Text}\\{selectedPlatform.ShortName}\\{selectedGame.ShortName}\\{selectedRegion}\\{string.Join("\\", patchShortNames)}";
                Directory.CreateDirectory(Path.Combine(outputDir));

                string newEbootPath = Path.Combine(outputDir, "eboot.bin");
                if (File.Exists(newEbootPath))
                    File.Delete(newEbootPath);
                File.Move(Path.Combine(buildDir, "eboot.bin"), newEbootPath);
                string newPKGPath = Path.Combine(outputDir, selectedGame.UpdatePKGName);
                PatchLog("Patched EBOOT moved to Output folder");
                if (File.Exists(newPKGPath))
                    File.Delete(newPKGPath);
                File.Move(pkgPath, newPKGPath);
                PatchLog("Update PKG moved to Output folder");
                return true;
            }
            return false;
        }

        private bool PatchEBOOTWithPPP(string ebootPath, List<string> patchShortNames)
        {
            // Make sure xdelta.exe is in PATH
            string xdelta = Path.Combine(Exe.Directory(), @"Dependencies\xdelta.exe");
            Directory.CreateDirectory(@"C:\.bin");
            File.Copy(xdelta, @"C:\.bin\xdelta.exe", true);
            var oldValue = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            if (!oldValue.Contains(@"C:\.bin"))
            {
                Environment.SetEnvironmentVariable("PATH", oldValue + @";C:\.bin", EnvironmentVariableTarget.User);
                PatchLog("Added xdelta.exe to user PATH variable");
            }

            // Un-fakesign EBOOT.BIN before patching if option is checked
            string elfPath = ebootPath;
            if (chk_Unfakesign.Checked)
            {
                elfPath = elfPath.Replace(".bin", ".elf");
                if (File.Exists(elfPath))
                    File.Delete(elfPath);
                using (FileSys.WaitForFile(ebootPath)) { };
                Exe.Run(pythonPath, $"\"{Path.Combine(Exe.Directory(), @"Dependencies\PS4\ppp\unfself.py")}\" \"{ebootPath}\"", false, Path.GetDirectoryName(ebootPath));
                using (FileSys.WaitForFile(elfPath)) { };
                if (!File.Exists(elfPath))
                {
                    PatchLog("Failed to un-fakesign eboot.bin");
                    return false;
                }
                PatchLog("Un-fakesigned eboot.bin");
                File.Delete(ebootPath);
            }

            // Patch EBOOT.ELF with selected patches
            string patchedEbootPath = elfPath + "--patched.bin";
            if (File.Exists(patchedEbootPath))
                File.Delete(patchedEbootPath);

            List<string> args = new() { elfPath, "--patch" };
            foreach (var patch in patchShortNames)
                args.Add(patch);
            ApplyPersonaPS4Patches(args.ToArray());

            using (FileSys.WaitForFile(patchedEbootPath)) { };
            if (File.Exists(patchedEbootPath))
            {
                if (File.Exists(ebootPath))
                    File.Delete(ebootPath);
                using (FileSys.WaitForFile(ebootPath)) { };
                File.Move(patchedEbootPath, ebootPath);

                return true;
            }
            return false;
        }
    }
}
