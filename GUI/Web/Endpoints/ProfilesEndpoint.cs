using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Windrose.Quartermaster.Core;

namespace Windrose.Quartermaster.Web.Endpoints;

public static class ProfilesEndpoint
{
    public static void Map(WebApplication app, string repoRoot)
    {
        var paths = WindrosePaths.FromModRoot(repoRoot);
        var store = new ProfileStore(paths);

        app.MapGet("/api/profiles", () =>
        {
            var summaries = store.LoadAll().Select(ToSummary).ToList();
            return Results.Json(summaries);
        });

        app.MapGet("/api/profiles/{id}", (string id) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            return Results.Json(profile, ProfileStore.JsonOpts);
        });

        app.MapPost("/api/profiles", async (HttpRequest req) =>
        {
            Profile incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<Profile>(req.Body, ProfileStore.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }
            if (incoming == null) return Results.BadRequest(new { error = "Empty body" });
            if (string.IsNullOrWhiteSpace(incoming.Name))
                return Results.BadRequest(new { error = "name is required" });

            // Server always assigns the id, ignoring any client-supplied value.
            incoming.Id = Guid.NewGuid().ToString();

            try { store.Save(incoming); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Created("/api/profiles/" + incoming.Id, incoming);
        });

        app.MapPut("/api/profiles/{id}", async (string id, HttpRequest req) =>
        {
            var existing = store.Load(id);
            if (existing == null) return Results.NotFound(new { error = "Profile not found", id });

            Profile incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<Profile>(req.Body, ProfileStore.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }
            if (incoming == null) return Results.BadRequest(new { error = "Empty body" });

            incoming.Id = id;
            incoming.CreatedAt = existing.CreatedAt;

            try { store.Save(incoming); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(incoming, ProfileStore.JsonOpts);
        });

        app.MapDelete("/api/profiles/{id}", (string id) =>
        {
            var existing = store.Load(id);
            if (existing == null) return Results.NotFound(new { error = "Profile not found", id });

            try
            {
                if (!store.Delete(id))
                    return Results.NotFound(new { error = "Profile file not found", id });

                var iconsDir = paths.ProfileIconsDir(id);
                if (Directory.Exists(iconsDir))
                {
                    try { Directory.Delete(iconsDir, recursive: true); }
                    catch { }
                }
                var shipMusicRoot = Path.Combine(
                    paths.Profiles, id, "ShipMusic");
                if (Directory.Exists(shipMusicRoot))
                {
                    try { Directory.Delete(shipMusicRoot, recursive: true); }
                    catch { }
                }
                var bonfireMusicRoot = paths.ProfileBonfireMusicDir(id);
                if (Directory.Exists(bonfireMusicRoot))
                {
                    try { Directory.Delete(bonfireMusicRoot, recursive: true); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.NoContent();
        });

        app.MapPost("/api/profiles/import", async (HttpRequest req, bool? overwrite) =>
        {
            Profile incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<Profile>(req.Body, ProfileStore.JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }
            if (incoming == null) return Results.BadRequest(new { error = "Empty body" });
            if (string.IsNullOrWhiteSpace(incoming.Id))
                return Results.BadRequest(new { error = "id is required" });
            if (string.IsNullOrWhiteSpace(incoming.Name))
                return Results.BadRequest(new { error = "name is required" });

            if (!IsSafeProfileId(incoming.Id))
                return Results.BadRequest(new { error = "id contains unsafe characters (only letters, digits, '-' and '_' allowed)" });

            var existing = store.Load(incoming.Id);
            if (existing != null && overwrite != true)
            {
                return Results.Json(new
                {
                    error = "A profile with this id already exists",
                    conflictId = existing.Id,
                    existingName = existing.Name,
                }, statusCode: StatusCodes.Status409Conflict);
            }

            if (existing != null)
            {
                incoming.CreatedAt = existing.CreatedAt;
            }

            try { store.Save(incoming); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return existing == null
                ? Results.Created("/api/profiles/" + incoming.Id, incoming)
                : Results.Json(incoming, ProfileStore.JsonOpts);
        });

        app.MapPost("/api/profiles/import-zip", async (HttpRequest req, bool? overwrite) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data with a 'file' field" });

            IFormFile zipFile;
            try
            {
                var form = await req.ReadFormAsync();
                zipFile = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid form: " + ex.Message });
            }
            if (zipFile == null || zipFile.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded (form key 'file' or first file)" });

            const long maxBytes = 500L * 1024 * 1024;
            if (zipFile.Length > maxBytes)
                return Results.BadRequest(new {
                    error = "File too large: " + zipFile.FileName
                          + " (" + zipFile.Length + " bytes, cap " + maxBytes + ")"
                });

            // Stage to memory so the ZipArchive can seek; the form stream is forward-only.
            byte[] zipBytes;
            using (var ms = new MemoryStream())
            {
                await zipFile.CopyToAsync(ms);
                zipBytes = ms.ToArray();
            }

            ZipArchive archive;
            try
            {
                archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read, leaveOpen: false);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Not a valid ZIP: " + ex.Message });
            }

            using (archive)
            {
                Profile incoming = null;
                string jsonEntryPath = null;
                foreach (var e in archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name)
                             && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Length))
                {
                    Profile parsed;
                    try
                    {
                        using var s = e.Open();
                        parsed = JsonSerializer.Deserialize<Profile>(s, ProfileStore.JsonOpts);
                    }
                    catch
                    {
                        continue;
                    }
                    if (parsed == null) continue;
                    if (string.IsNullOrWhiteSpace(parsed.Id)) continue;
                    if (string.IsNullOrWhiteSpace(parsed.Name)) continue;
                    var stem = Path.GetFileNameWithoutExtension(e.Name);
                    if (!string.Equals(stem, parsed.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    incoming = parsed;
                    jsonEntryPath = e.FullName;
                    break;
                }

                if (incoming == null || jsonEntryPath == null)
                    return Results.BadRequest(new { error = "No profile JSON found inside the ZIP (expected <id>.json with matching id + name fields)" });

                if (!IsSafeProfileId(incoming.Id))
                    return Results.BadRequest(new { error = "id contains unsafe characters (only letters, digits, '-' and '_' allowed)" });

                var existing = store.Load(incoming.Id);
                if (existing != null && overwrite != true)
                {
                    return Results.Json(new
                    {
                        error = "A profile with this id already exists",
                        conflictId = existing.Id,
                        existingName = existing.Name,
                    }, statusCode: StatusCodes.Status409Conflict);
                }

                if (existing != null)
                    incoming.CreatedAt = existing.CreatedAt;

                // ZipArchive uses '/' as the entry separator regardless of platform.
                var prefix = "";
                var slashIdx = jsonEntryPath.LastIndexOf('/');
                if (slashIdx >= 0) prefix = jsonEntryPath.Substring(0, slashIdx + 1);
                var subfolderPrefix = prefix + incoming.Id + "/";

                var profilesDir = paths.Profiles;
                Directory.CreateDirectory(profilesDir);
                var targetSubfolder = Path.Combine(profilesDir, incoming.Id);

                // On overwrite, wipe the existing subfolder so stale assets can't bleed through.
                if (existing != null && Directory.Exists(targetSubfolder))
                {
                    try { Directory.Delete(targetSubfolder, recursive: true); }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { error = "Could not clear existing subfolder for overwrite: " + ex.Message });
                    }
                }

                // Save the JSON before extracting assets so a failed extraction leaves a recoverable profile.
                try { store.Save(incoming); }
                catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

                int extractedFiles = 0;
                var targetRoot = Path.GetFullPath(targetSubfolder);
                foreach (var e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.FullName)) continue;
                    if (!e.FullName.StartsWith(subfolderPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var relative = e.FullName.Substring(subfolderPrefix.Length);
                    if (string.IsNullOrEmpty(relative)) continue;
                    var dest = Path.GetFullPath(Path.Combine(targetSubfolder,
                        relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(targetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(dest, targetRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // Zip-slip attempt: skip this entry rather than aborting the whole import.
                        continue;
                    }
                    if (e.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    using var src = e.Open();
                    using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                    await src.CopyToAsync(fs);
                    extractedFiles++;
                }

                return existing == null
                    ? Results.Created("/api/profiles/" + incoming.Id, new
                    {
                        profile = incoming,
                        extractedFiles,
                        subfolderFound = extractedFiles > 0,
                    })
                    : Results.Json(new
                    {
                        profile = incoming,
                        extractedFiles,
                        subfolderFound = extractedFiles > 0,
                    }, ProfileStore.JsonOpts);
            }
        });

        app.MapPost("/api/profiles/{id}/duplicate", (string id) =>
        {
            var src = store.Load(id);
            if (src == null) return Results.NotFound(new { error = "Profile not found", id });

            var clone = new Profile
            {
                Id = Guid.NewGuid().ToString(),
                Name = (src.Name ?? "Profile") + " (copy)",
                Description = src.Description,
                Globals = CloneGlobals(src.Globals),
                Overrides = src.Overrides == null
                    ? new Dictionary<string, ItemOverride>()
                    : src.Overrides.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value == null
                            ? null
                            : new ItemOverride { StackSize = kvp.Value.StackSize }),
                LootOverrides = CloneLootOverrides(src.LootOverrides),
                BuyerRecipes = CloneBuyerRecipes(src.BuyerRecipes),
                BuyerLists = CloneBuyerLists(src.BuyerLists),
                SellerRecipes = CloneSellerRecipes(src.SellerRecipes),
                SellerLists = CloneSellerLists(src.SellerLists),
                CustomItems = CloneCustomItems(src.CustomItems),
                CustomBuildings = CloneCustomBuildings(src.CustomBuildings),
            };

            try { store.Save(clone); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            // Mirror per-item icon bytes into the clone so it is self-contained.
            try
            {
                var srcIconsDir = paths.ProfileIconsDir(src.Id);
                if (Directory.Exists(srcIconsDir))
                {
                    var dstIconsDir = paths.ProfileIconsDir(clone.Id);
                    Directory.CreateDirectory(dstIconsDir);
                    foreach (var file in Directory.EnumerateFiles(srcIconsDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var fname = Path.GetFileName(file);
                        File.Copy(file, Path.Combine(dstIconsDir, fname), overwrite: true);
                    }
                }
            }
            catch { }

            // Mirror per-slot ShipMusic bytes under the clone's profile id.
            try
            {
                if (src.Globals != null && src.Globals.ShipMusic != null
                    && src.Globals.ShipMusic.Songs != null)
                {
                    foreach (var slotStem in src.Globals.ShipMusic.Songs.Keys)
                    {
                        var srcSlotDir = paths.ProfileShipMusicSlotDir(src.Id, slotStem);
                        if (!Directory.Exists(srcSlotDir)) continue;
                        var dstSlotDir = paths.ProfileShipMusicSlotDir(clone.Id, slotStem);
                        Directory.CreateDirectory(dstSlotDir);
                        foreach (var file in Directory.EnumerateFiles(srcSlotDir, "*", SearchOption.TopDirectoryOnly))
                        {
                            var fname = Path.GetFileName(file);
                            File.Copy(file, Path.Combine(dstSlotDir, fname), overwrite: true);
                        }
                    }
                }
            }
            catch { }

            // Mirror BonfireMusic bytes under the clone's profile id.
            try
            {
                if (src.Globals != null && src.Globals.BonfireMusic != null)
                {
                    var srcBmDir = paths.ProfileBonfireMusicDir(src.Id);
                    if (Directory.Exists(srcBmDir))
                    {
                        var dstBmDir = paths.ProfileBonfireMusicDir(clone.Id);
                        Directory.CreateDirectory(dstBmDir);
                        foreach (var file in Directory.EnumerateFiles(srcBmDir, "*", SearchOption.TopDirectoryOnly))
                        {
                            var fname = Path.GetFileName(file);
                            File.Copy(file, Path.Combine(dstBmDir, fname), overwrite: true);
                        }
                    }
                }
            }
            catch { }

            return Results.Created("/api/profiles/" + clone.Id, clone);
        });

        app.MapPost("/api/profiles/{id}/icons/{itemId}", async (string id, string itemId, HttpRequest req) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });

            var item = profile.CustomItems?.FirstOrDefault(c => c != null && string.Equals(c.Id, itemId, StringComparison.Ordinal));
            if (item == null) return Results.NotFound(new { error = "CustomItem not found in profile", itemId });

            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data" });

            IFormFile file;
            try
            {
                var form = await req.ReadFormAsync();
                file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid form: " + ex.Message });
            }
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded (form key 'file' or first file)" });

            const long maxBytes = 8L * 1024 * 1024;
            if (file.Length > maxBytes)
                return Results.BadRequest(new { error = $"File too large ({file.Length} bytes); max is {maxBytes} bytes" });

            var iconsDir = paths.ProfileIconsDir(id);
            Directory.CreateDirectory(iconsDir);
            var iconFileName = itemId + ".png";
            var diskPath = Path.Combine(iconsDir, iconFileName);

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            // Reject non-PNG bytes by checking the PNG signature.
            if (bytes.Length < 8
                || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47
                || bytes[4] != 0x0D || bytes[5] != 0x0A || bytes[6] != 0x1A || bytes[7] != 0x0A)
            {
                return Results.BadRequest(new { error = "File is not a PNG (magic mismatch)" });
            }

            await File.WriteAllBytesAsync(diskPath, bytes);

            item.IconPath = iconFileName;
            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(new { iconPath = iconFileName, size = bytes.Length });
        });

        app.MapDelete("/api/profiles/{id}/icons/{itemId}", (string id, string itemId) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });

            var item = profile.CustomItems?.FirstOrDefault(c => c != null && string.Equals(c.Id, itemId, StringComparison.Ordinal));
            if (item == null) return Results.NotFound(new { error = "CustomItem not found in profile", itemId });

            var iconsDir = paths.ProfileIconsDir(id);
            if (!string.IsNullOrEmpty(item.IconPath))
            {
                var diskPath = Path.Combine(iconsDir, item.IconPath);
                if (File.Exists(diskPath))
                {
                    try { File.Delete(diskPath); } catch { }
                }
            }

            item.IconPath = null;
            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.NoContent();
        });

        app.MapGet("/api/profiles/{id}/icons/{itemId}", (string id, string itemId) =>
        {
            // Reject path-traversal: filename-safe characters only.
            if (string.IsNullOrEmpty(itemId)) return Results.BadRequest(new { error = "itemId required" });
            foreach (var ch in itemId)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                    return Results.BadRequest(new { error = "itemId must be alnum + underscore" });
            }

            var iconsDir = paths.ProfileIconsDir(id);
            var diskPath = Path.Combine(iconsDir, itemId + ".png");
            if (!File.Exists(diskPath)) return Results.NotFound(new { error = "No icon for this item" });
            return Results.File(diskPath, "image/png");
        });

        app.MapGet("/api/profiles/{id}/ship-music", (string id) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });

            var songs = profile.Globals?.ShipMusic?.Songs;
            var excluded = profile.Globals?.ShipMusic?.ExcludedSlots;
            var excludedSet = excluded == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
            var rows = ShipMusicSlots.All.Select(slot =>
            {
                ShipMusicSlotOverride ov = null;
                if (songs != null) songs.TryGetValue(slot.Stem, out ov);
                bool wavPresent = false;
                long wavBytes = 0;
                if (ov != null)
                {
                    var slotDir = paths.ProfileShipMusicSlotDir(id, slot.Stem);
                    var wavPath = Path.Combine(slotDir, "audio.wav");
                    wavPresent = File.Exists(wavPath);
                    if (wavPresent)
                    {
                        try { wavBytes = new FileInfo(wavPath).Length; } catch { }
                    }
                }
                return new
                {
                    stem = slot.Stem,
                    title = slot.Title,
                    state = ov == null ? "vanilla"
                          : wavPresent ? "custom"
                          : "broken",
                    originalFilename = ov?.OriginalFilename,
                    wavBytes,
                    excluded = excludedSet.Contains(slot.Stem),
                    // Volume null or 0.45 both mean "vanilla unchanged".
                    volume = ov?.Volume ?? 0.45,
                };
            }).ToArray();
            return Results.Json(new { slots = rows });
        });

        app.MapPost("/api/profiles/{id}/ship-music/{slotStem}/exclude",
            (string id, string slotStem) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            if (!ShipMusicSlots.IsKnown(slotStem))
                return Results.BadRequest(new { error = "Unknown ship-music slot stem", slotStem });

            if (profile.Globals == null) profile.Globals = new ProfileGlobals();
            if (profile.Globals.ShipMusic == null) profile.Globals.ShipMusic = new ShipMusicGlobal();
            if (profile.Globals.ShipMusic.ExcludedSlots == null)
                profile.Globals.ShipMusic.ExcludedSlots = new List<string>();

            var already = profile.Globals.ShipMusic.ExcludedSlots
                .Any(s => string.Equals(s, slotStem, StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                // At least one track must stay active; an empty Shanty.Cues array crashes the engine.
                int excludedAfter = profile.Globals.ShipMusic.ExcludedSlots.Count + 1;
                int activeVanillaAfter = ShipMusicSlots.All.Count - excludedAfter;
                int addedCount = profile.Globals?.ShipMusicAdd?.Tracks == null
                    ? 0
                    : profile.Globals.ShipMusicAdd.Tracks.Count(t => t != null && !string.IsNullOrEmpty(t.TrackKey));
                if (activeVanillaAfter + addedCount < 1)
                {
                    return Results.BadRequest(new {
                        error = "Cannot exclude the last remaining shanty - at least one track "
                              + "(vanilla or added) must stay in the rotation, otherwise the engine "
                              + "crashes on an empty Shanty.Cues array."
                    });
                }
                profile.Globals.ShipMusic.ExcludedSlots.Add(slotStem);
            }

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.NoContent();
        });

        app.MapDelete("/api/profiles/{id}/ship-music/{slotStem}/exclude",
            (string id, string slotStem) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            if (!ShipMusicSlots.IsKnown(slotStem))
                return Results.BadRequest(new { error = "Unknown ship-music slot stem", slotStem });

            if (profile.Globals?.ShipMusic?.ExcludedSlots != null)
            {
                profile.Globals.ShipMusic.ExcludedSlots.RemoveAll(s =>
                    string.Equals(s, slotStem, StringComparison.OrdinalIgnoreCase));
            }

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.NoContent();
        });

        app.MapPost("/api/profiles/{id}/ship-music/{slotStem}/volume",
            async (string id, string slotStem, HttpRequest req) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            if (!ShipMusicSlots.ByStem.TryGetValue(slotStem, out var slot))
                return Results.BadRequest(new { error = "Unknown ship-music slot stem", slotStem });

            double volume;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
                if (!doc.RootElement.TryGetProperty("volume", out var vEl))
                    return Results.BadRequest(new { error = "Missing 'volume' field" });
                volume = vEl.GetDouble();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }

            if (volume < 0.0) volume = 0.0;
            if (volume > 1.0) volume = 1.0;

            if (profile.Globals == null) profile.Globals = new ProfileGlobals();
            if (profile.Globals.ShipMusic == null) profile.Globals.ShipMusic = new ShipMusicGlobal();
            if (profile.Globals.ShipMusic.Songs == null)
                profile.Globals.ShipMusic.Songs = new Dictionary<string, ShipMusicSlotOverride>(StringComparer.OrdinalIgnoreCase);

            if (!profile.Globals.ShipMusic.Songs.TryGetValue(slotStem, out var existing) || existing == null)
            {
                existing = new ShipMusicSlotOverride();
            }
            existing.Volume = volume;
            profile.Globals.ShipMusic.Songs[slotStem] = existing;

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(new { slotStem, volume });
        });

        app.MapPost("/api/profiles/{id}/ship-music/{slotStem}",
            async (string id, string slotStem, HttpRequest req) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });

            if (!ShipMusicSlots.ByStem.TryGetValue(slotStem, out var slot))
                return Results.BadRequest(new { error = "Unknown ship-music slot stem", slotStem });

            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data" });

            IFormFileCollection files;
            string originalFilename;
            try
            {
                var form = await req.ReadFormAsync();
                files = form.Files;
                originalFilename = form["filename"].ToString();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid form: " + ex.Message });
            }

            IFormFile audioFile = files.GetFile("audio")
                ?? files.GetFile("wav")
                ?? files.FirstOrDefault(f => f.FileName != null
                    && AudioPreprocessor.IsSupportedExtension(f.FileName));
            if (audioFile == null)
            {
                return Results.BadRequest(new {
                    error = "Need a single audio file (" + AudioPreprocessor.SupportedExtensionsList()
                          + "). The ship-music tab transcodes it to a 44.1 kHz "
                          + "stereo WAV via ffmpeg, encodes that to Bink Audio "
                          + "and splices the result into a SoundWave template at "
                          + "build time - no UE5 Editor cook needed."
                });
            }
            if (!AudioPreprocessor.IsSupportedExtension(audioFile.FileName))
            {
                return Results.BadRequest(new {
                    error = "Unsupported audio format: " + audioFile.FileName
                          + ". Allowed: " + AudioPreprocessor.SupportedExtensionsList() + "."
                });
            }

            const long maxBytes = 150L * 1024 * 1024;
            if (audioFile.Length > maxBytes)
                return Results.BadRequest(new {
                    error = "File too large: " + audioFile.FileName
                          + " (" + audioFile.Length + " bytes, cap " + maxBytes + ")"
                });

            // Stage to a temp file keeping the source extension so ffmpeg picks the right demuxer.
            var srcExt = Path.GetExtension(audioFile.FileName);
            if (string.IsNullOrEmpty(srcExt)) srcExt = ".bin";
            var stagedSrc = Path.Combine(Path.GetTempPath(),
                "qm_audio_" + Guid.NewGuid().ToString("N") + srcExt);
            await SaveFormFile(audioFile, stagedSrc);

            var slotDir = paths.ProfileShipMusicSlotDir(id, slotStem);
            Directory.CreateDirectory(slotDir);
            var wavOut = Path.Combine(slotDir, "audio.wav");

            AudioPreprocessor.Result prep;
            try
            {
                prep = await AudioPreprocessor.PreprocessAsync(
                    paths, stagedSrc, wavOut,
                    log: null);
            }
            catch (Exception ex)
            {
                try { File.Delete(stagedSrc); } catch { }
                try { if (File.Exists(wavOut)) File.Delete(wavOut); } catch { }
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                try { File.Delete(stagedSrc); } catch { }
            }

            WavInfo.Info wavInfo;
            try
            {
                wavInfo = WavInfo.Read(wavOut);
            }
            catch (Exception ex)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new {
                    error = "Preprocessed WAV failed validation: " + ex.Message
                          + " - this is a bug in the audio preprocessor."
                });
            }
            if (wavInfo.SampleRate != 44100 || wavInfo.Channels != 2 || wavInfo.BitsPerSample != 16)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new {
                    error = "Preprocessed WAV is not 44.1 kHz / stereo / 16-bit ("
                          + wavInfo.Describe() + ") - this is a bug in the audio preprocessor."
                });
            }

            if (string.IsNullOrEmpty(originalFilename))
                originalFilename = audioFile.FileName ?? slot.Stem + srcExt;

            if (profile.Globals == null) profile.Globals = new ProfileGlobals();
            if (profile.Globals.ShipMusic == null) profile.Globals.ShipMusic = new ShipMusicGlobal();
            if (profile.Globals.ShipMusic.Songs == null)
                profile.Globals.ShipMusic.Songs = new Dictionary<string, ShipMusicSlotOverride>(StringComparer.OrdinalIgnoreCase);
            // Preserve any existing Volume so a re-upload keeps the slider position.
            profile.Globals.ShipMusic.Songs.TryGetValue(slotStem, out var prior);
            profile.Globals.ShipMusic.Songs[slotStem] = new ShipMusicSlotOverride
            {
                OriginalFilename = originalFilename,
                Volume = prior?.Volume,
            };

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(new
            {
                stem = slotStem,
                title = slot.Title,
                originalFilename,
                wavBytes = new FileInfo(wavOut).Length,
                durationSeconds = wavInfo.DurationSeconds,
                transcoded = prep.WasTranscoded,
                sourceFormat = prep.SourceFormat,
            });
        });

        app.MapDelete("/api/profiles/{id}/ship-music/{slotStem}",
            (string id, string slotStem) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            if (!ShipMusicSlots.IsKnown(slotStem))
                return Results.BadRequest(new { error = "Unknown ship-music slot stem", slotStem });

            var slotDir = paths.ProfileShipMusicSlotDir(id, slotStem);
            if (Directory.Exists(slotDir))
            {
                try { Directory.Delete(slotDir, recursive: true); }
                catch { }
            }

            if (profile.Globals != null
                && profile.Globals.ShipMusic != null
                && profile.Globals.ShipMusic.Songs != null)
            {
                profile.Globals.ShipMusic.Songs.Remove(slotStem);
            }

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.NoContent();
        });

        app.MapGet("/api/profiles/{id}/ship-music-add", (string id) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            var tracks = profile.Globals?.ShipMusicAdd?.Tracks;
            var rows = (tracks ?? new List<ShipMusicAddedTrack>())
                .Select((t, idx) =>
                {
                    if (t == null) return null;
                    var trackDir = paths.ProfileShipMusicAddTrackDir(id, t.TrackKey ?? "");
                    var wavPath = Path.Combine(trackDir, "audio.wav");
                    bool wavPresent = !string.IsNullOrEmpty(t.TrackKey) && File.Exists(wavPath);
                    long wavBytes = 0;
                    if (wavPresent)
                    {
                        try { wavBytes = new FileInfo(wavPath).Length; } catch { }
                    }
                    return new
                    {
                        trackKey = t.TrackKey,
                        title = t.Title,
                        originalFilename = t.OriginalFilename,
                        newIndex = (idx + 11).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        state = wavPresent ? "ready" : "missing-wav",
                        wavBytes,
                        // Volume null or 0.45 both mean "vanilla unchanged".
                        volume = t.Volume ?? 0.45,
                    };
                })
                .Where(r => r != null)
                .ToArray();
            return Results.Json(new { tracks = rows });
        });

        app.MapPost("/api/profiles/{id}/ship-music-add", async (string id, HttpRequest req) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data" });

            IFormFileCollection files;
            string trackKey, title, originalFilename;
            try
            {
                var form = await req.ReadFormAsync();
                files = form.Files;
                trackKey = (form["trackKey"].ToString() ?? "").Trim();
                title = form["title"].ToString();
                originalFilename = form["filename"].ToString();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid form: " + ex.Message });
            }

            if (string.IsNullOrEmpty(trackKey))
                return Results.BadRequest(new { error = "trackKey is required (filesystem-safe identifier)" });
            foreach (var c in trackKey)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                      || (c >= '0' && c <= '9') || c == '_'))
                {
                    return Results.BadRequest(new { error = "trackKey contains characters outside [A-Za-z0-9_]" });
                }
            }

            IFormFile audioFile = files.GetFile("audio")
                ?? files.GetFile("wav")
                ?? files.FirstOrDefault(f => f.FileName != null
                    && AudioPreprocessor.IsSupportedExtension(f.FileName));
            if (audioFile == null)
            {
                return Results.BadRequest(new
                {
                    error = "Need a single audio file (" + AudioPreprocessor.SupportedExtensionsList()
                          + ")."
                });
            }
            if (!AudioPreprocessor.IsSupportedExtension(audioFile.FileName))
            {
                return Results.BadRequest(new
                {
                    error = "Unsupported audio format: " + audioFile.FileName
                          + ". Allowed: " + AudioPreprocessor.SupportedExtensionsList() + "."
                });
            }
            const long maxBytes = 150L * 1024 * 1024;
            if (audioFile.Length > maxBytes)
                return Results.BadRequest(new
                {
                    error = "File too large: " + audioFile.FileName
                          + " (" + audioFile.Length + " bytes, cap " + maxBytes + ")"
                });

            var srcExt = Path.GetExtension(audioFile.FileName);
            if (string.IsNullOrEmpty(srcExt)) srcExt = ".bin";
            var stagedSrc = Path.Combine(Path.GetTempPath(),
                "qm_audio_" + Guid.NewGuid().ToString("N") + srcExt);
            await SaveFormFile(audioFile, stagedSrc);

            var trackDir = paths.ProfileShipMusicAddTrackDir(id, trackKey);
            Directory.CreateDirectory(trackDir);
            var wavOut = Path.Combine(trackDir, "audio.wav");

            AudioPreprocessor.Result prep;
            try
            {
                prep = await AudioPreprocessor.PreprocessAsync(paths, stagedSrc, wavOut, log: null);
            }
            catch (Exception ex)
            {
                try { File.Delete(stagedSrc); } catch { }
                try { if (File.Exists(wavOut)) File.Delete(wavOut); } catch { }
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                try { File.Delete(stagedSrc); } catch { }
            }

            WavInfo.Info wavInfo;
            try { wavInfo = WavInfo.Read(wavOut); }
            catch (Exception ex)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new { error = "Preprocessed WAV failed validation: " + ex.Message });
            }
            if (wavInfo.SampleRate != 44100 || wavInfo.Channels != 2 || wavInfo.BitsPerSample != 16)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new
                {
                    error = "Preprocessed WAV is not 44.1 kHz / stereo / 16-bit ("
                          + wavInfo.Describe() + ")."
                });
            }

            if (string.IsNullOrEmpty(originalFilename))
                originalFilename = audioFile.FileName ?? (trackKey + srcExt);

            if (profile.Globals == null) profile.Globals = new ProfileGlobals();
            if (profile.Globals.ShipMusicAdd == null) profile.Globals.ShipMusicAdd = new ShipMusicAddGlobal();
            if (profile.Globals.ShipMusicAdd.Tracks == null)
                profile.Globals.ShipMusicAdd.Tracks = new List<ShipMusicAddedTrack>();

            var existing = profile.Globals.ShipMusicAdd.Tracks
                .FindIndex(t => t != null && string.Equals(t.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase));
            // Preserve any existing volume on replace; new tracks default to 0.45 (vanilla parity).
            double? volumeForEntry = null;
            if (existing >= 0)
            {
                var prior = profile.Globals.ShipMusicAdd.Tracks[existing];
                volumeForEntry = prior?.Volume;
            }
            if (!volumeForEntry.HasValue) volumeForEntry = 0.45;
            var entry = new ShipMusicAddedTrack
            {
                TrackKey = trackKey,
                Title = string.IsNullOrEmpty(title) ? null : title,
                OriginalFilename = originalFilename,
                Volume = volumeForEntry,
            };
            if (existing >= 0) profile.Globals.ShipMusicAdd.Tracks[existing] = entry;
            else profile.Globals.ShipMusicAdd.Tracks.Add(entry);

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(new
            {
                trackKey,
                title = entry.Title,
                originalFilename,
                wavBytes = new FileInfo(wavOut).Length,
                durationSeconds = wavInfo.DurationSeconds,
                transcoded = prep.WasTranscoded,
                sourceFormat = prep.SourceFormat,
            });
        });

        app.MapPost("/api/profiles/{id}/ship-music-add/{trackKey}/volume",
            async (string id, string trackKey, HttpRequest req) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            if (string.IsNullOrEmpty(trackKey))
                return Results.BadRequest(new { error = "trackKey is required" });

            double volume;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
                if (!doc.RootElement.TryGetProperty("volume", out var vEl))
                    return Results.BadRequest(new { error = "Missing 'volume' field" });
                volume = vEl.GetDouble();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid JSON: " + ex.Message });
            }
            if (volume < 0.0) volume = 0.0;
            if (volume > 1.0) volume = 1.0;

            var idx = profile.Globals?.ShipMusicAdd?.Tracks?
                .FindIndex(t => t != null && string.Equals(t.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase));
            if (idx == null || idx.Value < 0)
                return Results.BadRequest(new { error = "Added track not found: " + trackKey });

            profile.Globals.ShipMusicAdd.Tracks[idx.Value].Volume = volume;

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Json(new { trackKey, volume });
        });

        app.MapDelete("/api/profiles/{id}/ship-music-add/{trackKey}",
            (string id, string trackKey) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });
            if (string.IsNullOrEmpty(trackKey))
                return Results.BadRequest(new { error = "trackKey is required" });

            var trackDir = paths.ProfileShipMusicAddTrackDir(id, trackKey);
            if (Directory.Exists(trackDir))
            {
                try { Directory.Delete(trackDir, recursive: true); } catch { }
            }
            if (profile.Globals?.ShipMusicAdd?.Tracks != null)
            {
                profile.Globals.ShipMusicAdd.Tracks.RemoveAll(t =>
                    t != null && string.Equals(t.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase));
            }

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.NoContent();
        });

        static (Profile profile, CustomBuilding bldg, string error) LoadBuilding(
            ProfileStore st, string profileId, string buildingId)
        {
            var p = st.Load(profileId);
            if (p == null) return (null, null, "Profile not found: " + profileId);
            if (string.IsNullOrEmpty(buildingId))
                return (p, null, "buildingId is required");
            var bld = p.CustomBuildings?.FirstOrDefault(b =>
                b != null && string.Equals(b.Id, buildingId, StringComparison.Ordinal));
            if (bld == null) return (p, null, "Building not found in profile: " + buildingId);
            return (p, bld, null);
        }

        app.MapGet("/api/profiles/{id}/buildings/{bid}/audio",
            (string id, string bid) =>
        {
            var (profile, bldg, err) = LoadBuilding(store, id, bid);
            if (err != null) return Results.BadRequest(new { error = err });

            var dir = paths.ProfileBuildingAudioDir(id, bid);
            var wavPath = Path.Combine(dir, "audio.wav");
            bool wavPresent = File.Exists(wavPath);
            long wavBytes = 0;
            if (wavPresent)
            {
                try { wavBytes = new FileInfo(wavPath).Length; } catch { }
            }
            return Results.Json(new
            {
                buildingId = bid,
                rangeMeters = bldg.AudioRangeMeters > 0 ? bldg.AudioRangeMeters : 15.0,
                volume      = bldg.AudioVolume > 0 ? bldg.AudioVolume : 0.45,
                source = bldg.AudioSource == null ? null : new
                {
                    originalFilename = bldg.AudioSource.OriginalFilename,
                    durationSec      = bldg.AudioSource.DurationSec,
                    sampleRate       = bldg.AudioSource.SampleRate,
                    channels         = bldg.AudioSource.Channels,
                    sizeBytes        = bldg.AudioSource.SizeBytes,
                },
                state = wavPresent ? "ready" : "missing-wav",
                wavBytes,
            });
        });

        app.MapPost("/api/profiles/{id}/buildings/{bid}/audio",
            async (string id, string bid, HttpRequest req) =>
        {
            var (profile, bldg, err) = LoadBuilding(store, id, bid);
            if (err != null) return Results.BadRequest(new { error = err });

            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data" });

            IFormFileCollection files;
            try
            {
                var form = await req.ReadFormAsync();
                files = form.Files;
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid form: " + ex.Message });
            }

            IFormFile audioFile = files.GetFile("audio")
                ?? files.GetFile("wav")
                ?? files.FirstOrDefault(f => f.FileName != null
                    && AudioPreprocessor.IsSupportedExtension(f.FileName));
            if (audioFile == null)
            {
                return Results.BadRequest(new {
                    error = "Need a single audio file (" + AudioPreprocessor.SupportedExtensionsList()
                          + ")."
                });
            }
            if (!AudioPreprocessor.IsSupportedExtension(audioFile.FileName))
            {
                return Results.BadRequest(new {
                    error = "Unsupported audio format: " + audioFile.FileName
                          + ". Allowed: " + AudioPreprocessor.SupportedExtensionsList() + "."
                });
            }

            const long maxBytes = 60L * 1024 * 1024;
            if (audioFile.Length > maxBytes)
                return Results.BadRequest(new {
                    error = "File too large: " + audioFile.FileName
                          + " (" + audioFile.Length + " bytes, cap " + maxBytes + ")"
                });

            var srcExt = Path.GetExtension(audioFile.FileName);
            if (string.IsNullOrEmpty(srcExt)) srcExt = ".bin";
            var stagedSrc = Path.Combine(Path.GetTempPath(),
                "qm_bldgaudio_" + Guid.NewGuid().ToString("N") + srcExt);
            await SaveFormFile(audioFile, stagedSrc);

            var dir = paths.ProfileBuildingAudioDir(id, bid);
            Directory.CreateDirectory(dir);
            var wavOut = Path.Combine(dir, "audio.wav");

            AudioPreprocessor.Result prep;
            try
            {
                prep = await AudioPreprocessor.PreprocessAsync(
                    paths, stagedSrc, wavOut, log: null);
            }
            catch (Exception ex)
            {
                try { File.Delete(stagedSrc); } catch { }
                try { if (File.Exists(wavOut)) File.Delete(wavOut); } catch { }
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                try { File.Delete(stagedSrc); } catch { }
            }

            WavInfo.Info wavInfo;
            try { wavInfo = WavInfo.Read(wavOut); }
            catch (Exception ex)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new {
                    error = "Preprocessed WAV failed validation: " + ex.Message
                });
            }
            if (wavInfo.SampleRate != 44100 || wavInfo.Channels != 2 || wavInfo.BitsPerSample != 16)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new {
                    error = "Preprocessed WAV is not 44.1 kHz / stereo / 16-bit ("
                          + wavInfo.Describe() + ")"
                });
            }

            bldg.AudioSource = new AudioSourceMeta
            {
                OriginalFilename = audioFile.FileName,
                DurationSec      = (float)wavInfo.DurationSeconds,
                SampleRate       = wavInfo.SampleRate,
                Channels         = wavInfo.Channels,
                SizeBytes        = new FileInfo(wavOut).Length,
            };

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(new
            {
                buildingId = bid,
                source = new
                {
                    originalFilename = bldg.AudioSource.OriginalFilename,
                    durationSec      = bldg.AudioSource.DurationSec,
                    sampleRate       = bldg.AudioSource.SampleRate,
                    channels         = bldg.AudioSource.Channels,
                    sizeBytes        = bldg.AudioSource.SizeBytes,
                },
                transcoded = prep.WasTranscoded,
                sourceFormat = prep.SourceFormat,
            });
        });

        app.MapDelete("/api/profiles/{id}/buildings/{bid}/audio",
            (string id, string bid) =>
        {
            var (profile, bldg, err) = LoadBuilding(store, id, bid);
            if (err != null) return Results.BadRequest(new { error = err });

            var dir = paths.ProfileBuildingAudioDir(id, bid);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            bldg.AudioSource = null;

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.NoContent();
        });

        app.MapGet("/api/profiles/{id}/bonfire-music", (string id) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });

            var bm = profile.Globals?.BonfireMusic;
            var dir = paths.ProfileBonfireMusicDir(id);
            var wavPath = Path.Combine(dir, "audio.wav");
            bool wavPresent = File.Exists(wavPath);
            long wavBytes = 0;
            if (wavPresent)
            {
                try { wavBytes = new FileInfo(wavPath).Length; } catch { }
            }
            // Volume == 0 with no upload is the "muted-vanilla" sentinel: the build silences the slot.
            string stateStr;
            if (bm == null) stateStr = "vanilla";
            else if (wavPresent) stateStr = "custom";
            else if (string.IsNullOrEmpty(bm.OriginalFilename)
                     && (bm.Volume.HasValue && bm.Volume.Value <= 1e-4))
                stateStr = "muted-vanilla";
            else stateStr = "broken";
            return Results.Json(new
            {
                state = stateStr,
                originalFilename = bm?.OriginalFilename,
                volume = bm?.Volume,
                title = BonfireMusicSlot.Title,
                stem = BonfireMusicSlot.Stem,
                wavBytes,
            });
        });

        app.MapPost("/api/profiles/{id}/bonfire-music",
            async (string id, HttpRequest req) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });

            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data" });

            IFormFileCollection files;
            string originalFilename;
            try
            {
                var form = await req.ReadFormAsync();
                files = form.Files;
                originalFilename = form["filename"].ToString();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "Invalid form: " + ex.Message });
            }

            IFormFile audioFile = files.GetFile("audio")
                ?? files.GetFile("wav")
                ?? files.FirstOrDefault(f => f.FileName != null
                    && AudioPreprocessor.IsSupportedExtension(f.FileName));
            if (audioFile == null)
            {
                return Results.BadRequest(new {
                    error = "Need a single audio file (" + AudioPreprocessor.SupportedExtensionsList() + ")."
                });
            }
            if (!AudioPreprocessor.IsSupportedExtension(audioFile.FileName))
            {
                return Results.BadRequest(new {
                    error = "Unsupported audio format: " + audioFile.FileName
                          + ". Allowed: " + AudioPreprocessor.SupportedExtensionsList() + "."
                });
            }

            const long maxBytes = 150L * 1024 * 1024;
            if (audioFile.Length > maxBytes)
                return Results.BadRequest(new {
                    error = "File too large: " + audioFile.FileName
                          + " (" + audioFile.Length + " bytes, cap " + maxBytes + ")"
                });

            var srcExt = Path.GetExtension(audioFile.FileName);
            if (string.IsNullOrEmpty(srcExt)) srcExt = ".bin";
            var stagedSrc = Path.Combine(Path.GetTempPath(),
                "qm_hearth_" + Guid.NewGuid().ToString("N") + srcExt);
            await SaveFormFile(audioFile, stagedSrc);

            var dir = paths.ProfileBonfireMusicDir(id);
            Directory.CreateDirectory(dir);
            var wavOut = Path.Combine(dir, "audio.wav");

            AudioPreprocessor.Result prep;
            try
            {
                prep = await AudioPreprocessor.PreprocessAsync(
                    paths, stagedSrc, wavOut, log: null);
            }
            catch (Exception ex)
            {
                try { File.Delete(stagedSrc); } catch { }
                try { if (File.Exists(wavOut)) File.Delete(wavOut); } catch { }
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                try { File.Delete(stagedSrc); } catch { }
            }

            WavInfo.Info wavInfo;
            try { wavInfo = WavInfo.Read(wavOut); }
            catch (Exception ex)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new {
                    error = "Preprocessed WAV failed validation: " + ex.Message
                });
            }
            if (wavInfo.SampleRate != 44100 || wavInfo.Channels != 2 || wavInfo.BitsPerSample != 16)
            {
                try { File.Delete(wavOut); } catch { }
                return Results.BadRequest(new {
                    error = "Preprocessed WAV is not 44.1 kHz / stereo / 16-bit ("
                          + wavInfo.Describe() + ")"
                });
            }

            if (string.IsNullOrEmpty(originalFilename))
                originalFilename = audioFile.FileName ?? BonfireMusicSlot.Stem + srcExt;

            if (profile.Globals == null) profile.Globals = new ProfileGlobals();
            double? priorVolume = profile.Globals.BonfireMusic?.Volume;
            profile.Globals.BonfireMusic = new BonfireMusicGlobal
            {
                OriginalFilename = originalFilename,
                Volume = priorVolume,
            };

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Json(new
            {
                stem = BonfireMusicSlot.Stem,
                title = BonfireMusicSlot.Title,
                originalFilename,
                wavBytes = new FileInfo(wavOut).Length,
                durationSeconds = wavInfo.DurationSeconds,
                transcoded = prep.WasTranscoded,
                sourceFormat = prep.SourceFormat,
            });
        });

        app.MapDelete("/api/profiles/{id}/bonfire-music", (string id) =>
        {
            var profile = store.Load(id);
            if (profile == null) return Results.NotFound(new { error = "Profile not found", id });

            var dir = paths.ProfileBonfireMusicDir(id);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { }
            }

            if (profile.Globals != null)
            {
                profile.Globals.BonfireMusic = null;
            }

            try { store.Save(profile); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.NoContent();
        });
    }

    static async Task SaveFormFile(IFormFile file, string diskPath)
    {
        using var fs = new FileStream(diskPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await file.CopyToAsync(fs);
    }

    static ProfileGlobals CloneGlobals(ProfileGlobals g)
    {
        if (g == null) return null;
        return new ProfileGlobals
        {
            StackSize = g.StackSize == null
                ? null
                : new StackSizeGlobal
                {
                    Multiplier = g.StackSize.Multiplier,
                    Absolute = g.StackSize.Absolute,
                    Cap = g.StackSize.Cap,
                },
            Loot = g.Loot == null
                ? null
                : new LootGlobal
                {
                    ByCategory = g.Loot.ByCategory == null
                        ? null
                        : new Dictionary<string, double>(g.Loot.ByCategory),
                },
            PickupRadius = g.PickupRadius == null
                ? null
                : new PickupRadiusGlobal
                {
                    Multiplier = g.PickupRadius.Multiplier,
                },
            FastTravelBells = g.FastTravelBells == null
                ? null
                : new FastTravelBellsGlobal
                {
                    BellCap = g.FastTravelBells.BellCap,
                    SignalFireCap = g.FastTravelBells.SignalFireCap,
                },
            BuildingStability = g.BuildingStability == null
                ? null
                : new BuildingStabilityGlobal
                {
                    Enabled = g.BuildingStability.Enabled,
                },
            NoSmoke = g.NoSmoke == null
                ? null
                : new NoSmokeGlobal
                {
                    Campfire = g.NoSmoke.Campfire,
                    Furnace = g.NoSmoke.Furnace,
                    Kiln = g.NoSmoke.Kiln,
                },
            MinimapRange = g.MinimapRange == null
                ? null
                : new MinimapRangeGlobal
                {
                    Multiplier = g.MinimapRange.Multiplier,
                },
            BonfireRadius = g.BonfireRadius == null
                ? null
                : new BonfireRadiusGlobal
                {
                    Multiplier = g.BonfireRadius.Multiplier,
                },
            BonfireMusic = g.BonfireMusic == null
                ? null
                : new BonfireMusicGlobal
                {
                    OriginalFilename = g.BonfireMusic.OriginalFilename,
                    Volume = g.BonfireMusic.Volume,
                },
            PickaxeRange = g.PickaxeRange == null
                ? null
                : new PickaxeRangeGlobal
                {
                    Multiplier = g.PickaxeRange.Multiplier,
                },
            Cooldowns = g.Cooldowns == null
                ? null
                : new CooldownsGlobal
                {
                    ElixirMultiplier         = g.Cooldowns.ElixirMultiplier,
                    MedicineMultiplier       = g.Cooldowns.MedicineMultiplier,
                    RecallMultiplier         = g.Cooldowns.RecallMultiplier,
                    ShipRepairKitMultiplier  = g.Cooldowns.ShipRepairKitMultiplier,
                    BoarWhistleMultiplier    = g.Cooldowns.BoarWhistleMultiplier,
                    ShipSummonMultiplier     = g.Cooldowns.ShipSummonMultiplier,
                    RangedReloadMultiplier   = g.Cooldowns.RangedReloadMultiplier,
                    ShipCannonMultiplier     = g.Cooldowns.ShipCannonMultiplier,
                },
            ProductionTimes = g.ProductionTimes == null
                ? null
                : new ProductionTimesGlobal
                {
                    CropGrowthMultiplier    = g.ProductionTimes.CropGrowthMultiplier,
                    SmeltingMultiplier      = g.ProductionTimes.SmeltingMultiplier,
                    KilnMultiplier          = g.ProductionTimes.KilnMultiplier,
                    TanningMultiplier       = g.ProductionTimes.TanningMultiplier,
                    MillingMultiplier       = g.ProductionTimes.MillingMultiplier,
                    BuildingBitsMultiplier  = g.ProductionTimes.BuildingBitsMultiplier,
                    DecorationMultiplier    = g.ProductionTimes.DecorationMultiplier,
                    ArmorWeaponMultiplier   = g.ProductionTimes.ArmorWeaponMultiplier,
                    TradeOutpostMultiplier  = g.ProductionTimes.TradeOutpostMultiplier,
                    OtherMultiplier         = g.ProductionTimes.OtherMultiplier,
                },
            ShipMusic = g.ShipMusic == null
                ? null
                : new ShipMusicGlobal
                {
                    Songs = g.ShipMusic.Songs == null
                        ? null
                        : g.ShipMusic.Songs.ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value == null
                                ? null
                                : new ShipMusicSlotOverride
                                {
                                    OriginalFilename = kvp.Value.OriginalFilename,
                                }),
                    ExcludedSlots = g.ShipMusic.ExcludedSlots == null
                        ? null
                        : new List<string>(g.ShipMusic.ExcludedSlots),
                },
            ShipMusicAdd = g.ShipMusicAdd == null
                ? null
                : new ShipMusicAddGlobal
                {
                    Tracks = g.ShipMusicAdd.Tracks == null
                        ? null
                        : g.ShipMusicAdd.Tracks
                            .Where(t => t != null)
                            .Select(t => new ShipMusicAddedTrack
                            {
                                TrackKey = t.TrackKey,
                                Title = t.Title,
                                OriginalFilename = t.OriginalFilename,
                            })
                            .ToList(),
                },
        };
    }

    // Deep-clone so editing the clone never mutates the source profile's collections.
    static Dictionary<string, LootTableOverride> CloneLootOverrides(
        Dictionary<string, LootTableOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, LootTableOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new LootTableOverride
            {
                Entries = v.Entries == null
                    ? null
                    : v.Entries.ToDictionary(
                        e => e.Key,
                        e => e.Value == null
                            ? null
                            : new LootEntryEdit
                            {
                                Min = e.Value.Min,
                                Max = e.Value.Max,
                                Weight = e.Value.Weight,
                                LootItem = e.Value.LootItem,
                                LootTable = e.Value.LootTable,
                            }),
                Removed = v.Removed == null ? null : new List<int>(v.Removed),
                Added = v.Added == null
                    ? null
                    : v.Added.Select(a => a == null
                        ? null
                        : new LootEntry
                        {
                            Min = a.Min,
                            Max = a.Max,
                            Weight = a.Weight,
                            LootItem = a.LootItem,
                            LootTable = a.LootTable,
                        }).ToList(),
            };
        }
        return result;
    }

    static Dictionary<string, BuyerRecipeOverride> CloneBuyerRecipes(
        Dictionary<string, BuyerRecipeOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, BuyerRecipeOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new BuyerRecipeOverride
            {
                ItemPath = v.ItemPath,
                ItemCount = v.ItemCount,
                PayItemPath = v.PayItemPath,
                PayCount = v.PayCount,
                CraftRequirement = v.CraftRequirement,
                IsCustom = v.IsCustom,
            };
        }
        return result;
    }

    static Dictionary<string, SellerRecipeOverride> CloneSellerRecipes(
        Dictionary<string, SellerRecipeOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, SellerRecipeOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new SellerRecipeOverride
            {
                ItemPath = v.ItemPath,
                ItemCount = v.ItemCount,
                PayItemPath = v.PayItemPath,
                PayCount = v.PayCount,
                CraftRequirement = v.CraftRequirement,
                IsCustom = v.IsCustom,
            };
        }
        return result;
    }

    static List<CustomItem> CloneCustomItems(List<CustomItem> src)
    {
        if (src == null) return null;
        var result = new List<CustomItem>(src.Count);
        foreach (var c in src)
        {
            if (c == null) { result.Add(null); continue; }
            result.Add(new CustomItem
            {
                Id = c.Id,
                TemplateId = c.TemplateId,
                Name = c.Name,
                Description = c.Description,
                MaxCountInSlot = c.MaxCountInSlot,
                Rarity = c.Rarity,
                KeepInInventoryOnDeath = c.KeepInInventoryOnDeath,
                ItemTexture = c.ItemTexture,
                VanityText = c.VanityText,
                IconPath = c.IconPath,
            });
        }
        return result;
    }

    static List<CustomBuilding> CloneCustomBuildings(List<CustomBuilding> src)
    {
        if (src == null) return null;
        var result = new List<CustomBuilding>(src.Count);
        foreach (var b in src)
        {
            if (b == null) { result.Add(null); continue; }
            var resolvedPrefix = !string.IsNullOrWhiteSpace(b.AssetPrefix)
                ? b.AssetPrefix
                : CustomBuilding.DeriveAssetPrefixFromMeshStem(b.MeshStem);
            result.Add(new CustomBuilding
            {
                Id = b.Id,
                TemplateId = b.TemplateId,
                Name = b.Name,
                Description = b.Description,
                CookedFolderPath = b.CookedFolderPath,
                AssetPrefix = resolvedPrefix,
                MeshStem = b.MeshStem,
                IconStem = b.IconStem,
                Slots = CloneCustomBuildingSlots(b.Slots),
                ComponentPresetId = b.ComponentPresetId,
                AudioRangeMeters = b.AudioRangeMeters,
                AudioVolume      = b.AudioVolume,
                AudioSource = b.AudioSource == null
                    ? null
                    : new AudioSourceMeta
                    {
                        OriginalFilename = b.AudioSource.OriginalFilename,
                        DurationSec      = b.AudioSource.DurationSec,
                        SampleRate       = b.AudioSource.SampleRate,
                        Channels         = b.AudioSource.Channels,
                        SizeBytes        = b.AudioSource.SizeBytes,
                    },
            });
        }
        return result;
    }

    static Dictionary<string, CustomBuildingSlot> CloneCustomBuildingSlots(
        Dictionary<string, CustomBuildingSlot> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, CustomBuildingSlot>(src.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new CustomBuildingSlot
            {
                VanillaMaterialParentPath = v.VanillaMaterialParentPath,
                ScalarParams  = v.ScalarParams  == null ? null : new Dictionary<string, float>(v.ScalarParams, StringComparer.Ordinal),
                VectorParams  = v.VectorParams  == null ? null : CloneVectorParams(v.VectorParams),
                TextureParams = v.TextureParams == null ? null : new Dictionary<string, string>(v.TextureParams, StringComparer.Ordinal),
            };
        }
        return result;
    }

    static Dictionary<string, float[]> CloneVectorParams(Dictionary<string, float[]> src)
    {
        var result = new Dictionary<string, float[]>(src.Count, StringComparer.Ordinal);
        foreach (var kvp in src)
        {
            result[kvp.Key] = kvp.Value == null ? null : (float[])kvp.Value.Clone();
        }
        return result;
    }

    static Dictionary<string, BuyerListOverride> CloneBuyerLists(
        Dictionary<string, BuyerListOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, BuyerListOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new BuyerListOverride
            {
                AddedRecipeIds = v.AddedRecipeIds == null ? null : new List<string>(v.AddedRecipeIds),
                RemovedRecipeIds = v.RemovedRecipeIds == null ? null : new List<string>(v.RemovedRecipeIds),
                RecipeOrder = v.RecipeOrder == null ? null : new List<string>(v.RecipeOrder),
            };
        }
        return result;
    }

    static Dictionary<string, SellerListOverride> CloneSellerLists(
        Dictionary<string, SellerListOverride> src)
    {
        if (src == null) return null;
        var result = new Dictionary<string, SellerListOverride>(src.Count);
        foreach (var kvp in src)
        {
            var v = kvp.Value;
            if (v == null) { result[kvp.Key] = null; continue; }
            result[kvp.Key] = new SellerListOverride
            {
                AddedRecipeIds = v.AddedRecipeIds == null ? null : new List<string>(v.AddedRecipeIds),
                RemovedRecipeIds = v.RemovedRecipeIds == null ? null : new List<string>(v.RemovedRecipeIds),
                RecipeOrder = v.RecipeOrder == null ? null : new List<string>(v.RecipeOrder),
            };
        }
        return result;
    }

    static object ToSummary(Profile p)
    {
        return new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            createdAt = p.CreatedAt,
            modifiedAt = p.ModifiedAt,
            overrideCount = p.Overrides == null ? 0 : p.Overrides.Count,
            lootOverrideCount = p.LootOverrides == null ? 0 : p.LootOverrides.Count,
            buyerRecipeCount = p.BuyerRecipes == null ? 0 : p.BuyerRecipes.Count,
            buyerListCount = p.BuyerLists == null ? 0 : p.BuyerLists.Count,
            sellerRecipeCount = p.SellerRecipes == null ? 0 : p.SellerRecipes.Count,
            sellerListCount = p.SellerLists == null ? 0 : p.SellerLists.Count,
            customItemCount = p.CustomItems == null ? 0 : p.CustomItems.Count,
            customBuildingCount = p.CustomBuildings == null ? 0 : p.CustomBuildings.Count,
            hasGlobalStackSize = p.Globals != null && p.Globals.StackSize != null
                                 && (p.Globals.StackSize.Multiplier.HasValue
                                     || p.Globals.StackSize.Absolute.HasValue),
            hasGlobalLoot = p.Globals != null && p.Globals.Loot != null
                            && p.Globals.Loot.ByCategory != null
                            && p.Globals.Loot.ByCategory.Count > 0,
            hasGlobalPickupRadius = p.Globals != null && p.Globals.PickupRadius != null
                                    && p.Globals.PickupRadius.Multiplier.HasValue
                                    && Math.Abs(p.Globals.PickupRadius.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalFastTravelBells = HasFastTravelBellsConfig(p),
            hasGlobalBuildingStability = p.Globals != null
                                         && p.Globals.BuildingStability != null
                                         && p.Globals.BuildingStability.Enabled.GetValueOrDefault(false),
            hasGlobalNoSmoke = HasAnyNoSmokeCategory(p),
            hasGlobalMinimapRange = p.Globals != null
                                    && p.Globals.MinimapRange != null
                                    && p.Globals.MinimapRange.Multiplier.HasValue
                                    && Math.Abs(p.Globals.MinimapRange.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalBonfireRadius = p.Globals != null
                                     && p.Globals.BonfireRadius != null
                                     && p.Globals.BonfireRadius.Multiplier.HasValue
                                     && Math.Abs(p.Globals.BonfireRadius.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalPickaxeRange = p.Globals != null
                                    && p.Globals.PickaxeRange != null
                                    && p.Globals.PickaxeRange.Multiplier.HasValue
                                    && Math.Abs(p.Globals.PickaxeRange.Multiplier.Value - 1.0) > 1e-9,
            hasGlobalCooldowns = p.Globals != null
                                 && p.Globals.Cooldowns != null
                                 && AnyCooldownActive(p.Globals.Cooldowns),
            hasGlobalProductionTimes = p.Globals != null
                                       && p.Globals.ProductionTimes != null
                                       && AnyProductionTimeActive(p.Globals.ProductionTimes),
            hasGlobalShipMusic = p.Globals != null
                                 && p.Globals.ShipMusic != null
                                 && p.Globals.ShipMusic.Songs != null
                                 && p.Globals.ShipMusic.Songs.Count > 0,
        };
    }

    static bool AnyCooldownActive(CooldownsGlobal cd)
    {
        return IsActive(cd.ElixirMultiplier)
            || IsActive(cd.MedicineMultiplier)
            || IsActive(cd.RecallMultiplier)
            || IsActive(cd.ShipRepairKitMultiplier)
            || IsActive(cd.BoarWhistleMultiplier)
            || IsActive(cd.ShipSummonMultiplier)
            || IsActive(cd.RangedReloadMultiplier)
            || IsActive(cd.ShipCannonMultiplier);
    }

    static bool AnyProductionTimeActive(ProductionTimesGlobal pt)
    {
        return IsActive(pt.CropGrowthMultiplier)
            || IsActive(pt.SmeltingMultiplier)
            || IsActive(pt.KilnMultiplier)
            || IsActive(pt.TanningMultiplier)
            || IsActive(pt.MillingMultiplier)
            || IsActive(pt.BuildingBitsMultiplier)
            || IsActive(pt.DecorationMultiplier)
            || IsActive(pt.ArmorWeaponMultiplier)
            || IsActive(pt.TradeOutpostMultiplier)
            || IsActive(pt.OtherMultiplier);
    }

    static bool IsActive(double? m)
    {
        return m.HasValue && Math.Abs(m.Value - 1.0) > 1e-9;
    }

    static bool HasAnyNoSmokeCategory(Profile p)
    {
        var n = p.Globals != null ? p.Globals.NoSmoke : null;
        if (n == null) return false;
        return n.Campfire.GetValueOrDefault(false)
            || n.Furnace.GetValueOrDefault(false)
            || n.Kiln.GetValueOrDefault(false);
    }

    // The id becomes a filename, so reject path-traversal and Win32-reserved names.
    static bool IsSafeProfileId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length > 128) return false;
        foreach (var ch in id)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                return false;
        }
        switch (id.ToUpperInvariant())
        {
            case "CON":  case "PRN":  case "AUX":  case "NUL":
            case "COM1": case "COM2": case "COM3": case "COM4":
            case "COM5": case "COM6": case "COM7": case "COM8": case "COM9":
            case "LPT1": case "LPT2": case "LPT3": case "LPT4":
            case "LPT5": case "LPT6": case "LPT7": case "LPT8": case "LPT9":
                return false;
        }
        return true;
    }

    static bool HasFastTravelBellsConfig(Profile p)
    {
        var b = p.Globals != null ? p.Globals.FastTravelBells : null;
        if (b == null) return false;
        if (b.BellCap.HasValue && b.BellCap.Value != BellLimitsPatcher.VanillaBellCap)
            return true;
        if (b.SignalFireCap.HasValue && b.SignalFireCap.Value != BellLimitsPatcher.VanillaSignalFireCap)
            return true;
        return false;
    }

}
