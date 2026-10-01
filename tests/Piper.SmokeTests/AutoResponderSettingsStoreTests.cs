using System.Diagnostics;
using System.Text;
using Piper.Core.Proxy;

internal static class AutoResponderSettingsStoreTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await RunRoundTripAsync(runner);
        await RunHostileContentAsync(runner);
        await RunSizeBoundsAsync(runner);
        await RunSaveFailureAsync(runner);
        await RunRuleCountDuringReadAsync(runner);
        await RunStaleTemporariesAsync(runner);
        await RunUserFacingTextAsync(runner);
        await RunAppendLimitsAsync(runner);
        await RunSweepOnceAsync(runner);
        await RunFileProblemPolicyAsync(runner);
        await RunSetAsideAsync(runner);
        await RunAppendAsync(runner);
        await RunHostileExpressionsAsync(runner);
    }

    private static Task RunRoundTripAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules persist in order", () =>
    {
        var path = TempPath();
        try
        {
            var saved = new AutoResponderSettings
            {
                Enabled = true,
                PassthroughUnmatched = false,
                Rules =
                [
                    new AutoResponderRule { Match = "EXACT:https://a.example.com/", Action = "*404" },
                    new AutoResponderRule { Enabled = false, Match = "/slow", Action = "*delay:2000" },
                    new AutoResponderRule
                    {
                        Match = "REGEX:/v(?<n>\\d+)/",
                        Action = "*inline",
                        Body = """{"stub":true}""",
                        ContentType = "application/json",
                        Comment = "stub the versioned API",
                    },
                ],
            };

            runner.IsTrue(AutoResponderSettingsStore.Save(saved, path).Succeeded, "a rule set can be saved");
            var loaded = AutoResponderSettingsStore.Load(path);
            var restored = loaded.Settings;

            runner.AreEqual(AutoResponderLoadStatus.Loaded, loaded.Status, "saved rules can be loaded");
            runner.AreEqual(true, restored!.Enabled, "master toggle");
            runner.AreEqual(false, restored.PassthroughUnmatched, "passthrough toggle");
            runner.AreEqual(3, restored.Rules.Count, "rule count");

            // Order is the semantics -- first match wins, so a reordered rule set is a different one.
            runner.AreEqual("EXACT:https://a.example.com/", restored.Rules[0].Match, "first rule kept its place");
            runner.AreEqual("/slow", restored.Rules[1].Match, "second rule kept its place");
            runner.AreEqual(false, restored.Rules[1].Enabled, "a disabled rule stays disabled");

            runner.AreEqual("*inline", restored.Rules[2].Action, "action");
            runner.AreEqual("""{"stub":true}""", restored.Rules[2].Body, "inline body travels with the rule");
            runner.AreEqual("application/json", restored.Rules[2].ContentType, "content type");
            runner.AreEqual("stub the versioned API", restored.Rules[2].Comment, "comment");
            runner.AreEqual(saved.Rules[2].Id, restored.Rules[2].Id, "ids survive, so hit counts survive an edit");

            // A rule hand-written into the file without an id must still get one.
            File.WriteAllText(path, """{"Enabled":true,"Rules":[{"Match":"/x","Action":"*404"}]}""");
            var handEdited = AutoResponderSettingsStore.Load(path).Settings;
            runner.IsTrue(!string.IsNullOrWhiteSpace(handEdited!.Rules[0].Id), "a hand-written rule is given an id");

            // A file another editor saved with a byte order mark is still a rule set.
            File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("""{"Enabled":true}""")]);
            runner.AreEqual(AutoResponderLoadStatus.Loaded, AutoResponderSettingsStore.Load(path).Status,
                "a UTF-8 byte order mark is tolerated");
        }
        finally
        {
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    // One bad autoresponder-rules.json used to crash every launch: the store is loaded in the main
    // form's constructor and a null anywhere in the file threw NullReferenceException from there.
    private static Task RunHostileContentAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: hostile content", () =>
    {
        var path = TempPath();
        try
        {
            var nullRules = Load(path, """{"Enabled":true,"Rules":null}""");
            runner.AreEqual(AutoResponderLoadStatus.Loaded, nullRules.Status, "\"Rules\": null still loads");
            runner.AreEqual(0, nullRules.Settings!.Rules.Count, "with no rules");
            runner.AreEqual(true, nullRules.Settings.Enabled, "and the toggles intact");

            var nullElement = Load(path, """{"Rules":[null,{"Match":"/keep","Action":"*404"},null]}""");
            runner.AreEqual(AutoResponderLoadStatus.Loaded, nullElement.Status, "a null rule loads");
            runner.AreEqual(1, nullElement.Settings!.Rules.Count, "null rules are dropped");
            runner.AreEqual("/keep", nullElement.Settings.Rules[0].Match, "the real rule survives");

            var nullFields = Load(path, """{"Rules":[{"Id":null,"Match":null,"Action":null,"Body":null}]}""");
            var rule = nullFields.Settings!.Rules[0];
            runner.AreEqual(string.Empty, rule.Match, "a null Match is an empty one");
            runner.AreEqual(string.Empty, rule.Action, "a null Action is an empty one");
            runner.IsTrue(!string.IsNullOrWhiteSpace(rule.Id), "a null Id is replaced");

            // Everything downstream of the store must cope too: the engine is applied from the same
            // constructor, and the panel clones every rule.
            var responder = new AutoResponder();
            responder.Apply(nullElement.Settings);
            responder.Apply(nullFields.Settings);
            runner.AreEqual(1, responder.Export().Rules.Count, "a rule set from a hostile file applies");
            var direct = new AutoResponderSettings { Rules = null! };
            runner.AreEqual(0, direct.Rules.Count, "assigning null Rules leaves an empty list");
            direct.Rules = [null!, new AutoResponderRule()];
            runner.AreEqual(1, direct.Clone().Rules.Count, "assigning a null element drops it");

            var duplicate = Load(path,
                """{"Rules":[{"Id":"a","Match":"1"},{"Id":"a","Match":"2"},{"Id":"a","Match":"3"}]}""");
            runner.AreEqual(3, duplicate.Settings!.Rules.Select(r => r.Id).Distinct().Count(),
                "rules sharing an id get their own, so hit counts stay separate");
            runner.AreEqual("a", duplicate.Settings.Rules[0].Id, "the first keeps the id it had");

            foreach (var (text, what) in new[]
            {
                ("""{"Enabled":true,"Rules":[{"Match":"/x","Act""", "truncated JSON"),
                ("", "an empty file"),
                ("   ", "a blank file"),
                ("null", "a top-level null"),
                ("[]", "a top-level array"),
                ("\"rules\"", "a top-level string"),
                ("""{"Rules":5}""", "a number for Rules"),
                ("""{"Rules":{"Match":"/x"}}""", "an object for Rules"),
                ("""{"Rules":["text"]}""", "a string for a rule"),
                ("""{"Enabled":"yes"}""", "a string for a boolean"),
                ("""{"Rules":[{"Match":5}]}""", "a number for Match"),
                (string.Concat(Enumerable.Repeat("[", 100_000)), "100,000 levels of nesting"),
            })
            {
                var result = Load(path, text);
                runner.AreEqual(AutoResponderLoadStatus.Malformed, result.Status, $"{what} is reported as malformed");
                runner.IsTrue(result.Settings is null, $"{what} yields no settings");
            }

            // Notepad's "Unicode" and friends: a byte order mark says how the file is encoded, and
            // File.ReadAllText honoured it, so these loaded before the byte cap was added.
            foreach (var (encoding, name) in new (Encoding, string)[]
            {
                (new UTF8Encoding(true), "UTF-8 with a BOM"),
                (new UnicodeEncoding(false, true), "UTF-16 little-endian with a BOM"),
                (new UnicodeEncoding(true, true), "UTF-16 big-endian with a BOM"),
                (new UTF32Encoding(false, true), "UTF-32 with a BOM"),
            })
            {
                File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes("""{"Enabled":true,"Rules":[{"Match":"/é","Action":"*404"}]}""")]);
                var decoded = AutoResponderSettingsStore.Load(path);
                runner.AreEqual(AutoResponderLoadStatus.Loaded, decoded.Status, $"{name} loads");
                runner.AreEqual("/é", decoded.Settings?.Rules.FirstOrDefault()?.Match, $"{name} keeps non-ASCII text");
            }

            File.WriteAllBytes(path, Encoding.Unicode.GetBytes("""{"Enabled":true}"""));
            runner.AreEqual(AutoResponderLoadStatus.Malformed, AutoResponderSettingsStore.Load(path).Status,
                "UTF-16 with no byte order mark cannot be told from garbage, as before");

            // Truncated exactly as a crash mid-write used to leave a rule set.
            runner.IsTrue(AutoResponderSettingsStore.Save(new AutoResponderSettings
            {
                Rules = [new AutoResponderRule { Match = "/a", Action = "*404" }, new AutoResponderRule { Match = "/b" }],
            }, path).Succeeded, "saved");
            var whole = File.ReadAllBytes(path);
            File.WriteAllBytes(path, whole[..(whole.Length / 2)]);
            runner.AreEqual(AutoResponderLoadStatus.Malformed, AutoResponderSettingsStore.Load(path).Status,
                "half of a saved file is malformed, not a crash");

            DeleteAll(path);
            runner.AreEqual(AutoResponderLoadStatus.Missing, AutoResponderSettingsStore.Load(path).Status,
                "no file is Missing, which is not an error");
            runner.AreEqual(AutoResponderLoadStatus.Missing,
                AutoResponderSettingsStore.Load(Path.Combine(path, "no", "such", "dir.json")).Status,
                "a missing directory is Missing too");

            var directory = Path.Combine(Path.GetTempPath(), $"piper-autoresponder-dir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                runner.AreEqual(AutoResponderLoadStatus.Unreadable, AutoResponderSettingsStore.Load(directory).Status,
                    "a directory where the file should be is Unreadable, not an exception");
            }
            finally
            {
                Directory.Delete(directory);
            }
        }
        finally
        {
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    private static Task RunSizeBoundsAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: size and rule-count caps", () =>
    {
        var path = TempPath();
        try
        {
            // Unknown properties are ignored, so padding is a way to hit an exact byte count with a
            // document that is otherwise valid.
            static byte[] PaddedTo(long size)
            {
                var head = Encoding.UTF8.GetBytes("{\"Enabled\":true,\"Padding\":\"");
                var tail = Encoding.UTF8.GetBytes("\"}");
                var bytes = new byte[size];
                Array.Copy(head, bytes, head.Length);
                Array.Fill(bytes, (byte)'a', head.Length, (int)size - head.Length - tail.Length);
                Array.Copy(tail, 0, bytes, size - tail.Length, tail.Length);
                return bytes;
            }

            File.WriteAllBytes(path, PaddedTo(AutoResponderSettingsStore.MaxFileBytes));
            runner.AreEqual(AutoResponderLoadStatus.Loaded, AutoResponderSettingsStore.Load(path).Status,
                "a file exactly at the byte cap loads");

            File.WriteAllBytes(path, PaddedTo(AutoResponderSettingsStore.MaxFileBytes + 1));
            var tooLarge = AutoResponderSettingsStore.Load(path);
            runner.AreEqual(AutoResponderLoadStatus.TooLarge, tooLarge.Status, "one byte over the cap is TooLarge");
            runner.IsTrue(tooLarge.Settings is null, "and none of it is used");

            // A much larger file must be refused without being read whole: garbage, not even JSON.
            using (var big = new FileStream(path, FileMode.Create))
            {
                big.SetLength(AutoResponderSettingsStore.MaxFileBytes * 8);
            }

            runner.AreEqual(AutoResponderLoadStatus.TooLarge, AutoResponderSettingsStore.Load(path).Status,
                "a file eight times the cap is TooLarge");

            static string RulesJson(int count) =>
                "{\"Rules\":[" + string.Join(",", Enumerable.Repeat("{\"Match\":\"/x\",\"Action\":\"*404\"}", count)) + "]}";

            File.WriteAllText(path, RulesJson(AutoResponderSettingsStore.MaxRules));
            var atCap = AutoResponderSettingsStore.Load(path);
            runner.AreEqual(AutoResponderLoadStatus.Loaded, atCap.Status, "exactly MaxRules rules load");
            runner.AreEqual(AutoResponderSettingsStore.MaxRules, atCap.Settings!.Rules.Count, "all of them");
            runner.AreEqual(AutoResponderSettingsStore.MaxRules, atCap.Settings.Rules.Select(r => r.Id).Distinct().Count(),
                "each with its own id");

            File.WriteAllText(path, RulesJson(AutoResponderSettingsStore.MaxRules + 1));
            runner.AreEqual(AutoResponderLoadStatus.TooManyRules, AutoResponderSettingsStore.Load(path).Status,
                "one rule over the cap is TooManyRules");

            // Save refuses what Load would refuse, so a rule set is never written that cannot be read.
            DeleteAll(path);
            var manyRules = new AutoResponderSettings
            {
                Rules = [.. Enumerable.Range(0, AutoResponderSettingsStore.MaxRules + 1).Select(_ => new AutoResponderRule())],
            };
            runner.AreEqual(AutoResponderSaveStatus.TooManyRules, AutoResponderSettingsStore.Save(manyRules, path).Status,
                "saving over the rule cap is refused");
            runner.IsTrue(!File.Exists(path), "and writes nothing");

            var hugeBody = new AutoResponderSettings
            {
                Rules = [new AutoResponderRule { Match = "/x", Action = "*inline", Body = new string('b', (int)AutoResponderSettingsStore.MaxFileBytes) }],
            };
            runner.AreEqual(AutoResponderSaveStatus.TooLarge, AutoResponderSettingsStore.Save(hugeBody, path).Status,
                "saving over the byte cap is refused");
            runner.IsTrue(!File.Exists(path), "and writes nothing");

            manyRules.Rules.RemoveAt(0);
            runner.IsTrue(AutoResponderSettingsStore.Save(manyRules, path).Succeeded, "a full rule set saves");
            runner.AreEqual(AutoResponderSettingsStore.MaxRules, AutoResponderSettingsStore.Load(path).Settings!.Rules.Count,
                "and loads back whole");
        }
        finally
        {
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    // Save used to swallow every IO error and return void, so Export "succeeded" into a read-only
    // folder and the rules the user believed were saved were gone at the next launch.
    private static Task RunSaveFailureAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: a failed save is reported", () =>
    {
        var path = TempPath();
        var blocker = TempPath();
        try
        {
            var original = new AutoResponderSettings { Rules = [new AutoResponderRule { Match = "/keep", Action = "*404" }] };
            runner.IsTrue(AutoResponderSettingsStore.Save(original, path).Succeeded, "first save");
            var before = File.ReadAllBytes(path);

            File.SetAttributes(path, FileAttributes.ReadOnly);
            AutoResponderSaveResult result;
            try
            {
                result = AutoResponderSettingsStore.Save(new AutoResponderSettings(), path);
                runner.AreEqual(AutoResponderSaveStatus.Failed, result.Status, "a read-only file refuses the save");
                runner.IsTrue(!string.IsNullOrEmpty(result.Detail), "with the system's reason");
                runner.IsTrue(!result.Succeeded, "Succeeded is false");
                runner.IsTrue(before.SequenceEqual(File.ReadAllBytes(path)), "the previous rules are untouched");
                runner.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Length,
                    "no temporary file is left behind");
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            // A directory where the file should go, and a file where the folder should go.
            File.Delete(path);
            Directory.CreateDirectory(path);
            try
            {
                runner.AreEqual(AutoResponderSaveStatus.Failed, AutoResponderSettingsStore.Save(original, path).Status,
                    "a directory in the way refuses the save");
            }
            finally
            {
                Directory.Delete(path);
            }

            File.WriteAllText(blocker, "not a folder");
            runner.AreEqual(AutoResponderSaveStatus.Failed,
                AutoResponderSettingsStore.Save(original, Path.Combine(blocker, "rules.json")).Status,
                "a folder that cannot be created refuses the save");

            // An export over an existing file replaces it whole.
            runner.IsTrue(AutoResponderSettingsStore.Save(original, path).Succeeded, "saved again");
            runner.IsTrue(AutoResponderSettingsStore.Save(new AutoResponderSettings { Enabled = true }, path).Succeeded,
                "and overwritten");
            runner.AreEqual(true, AutoResponderSettingsStore.Load(path).Settings!.Enabled, "with the new content");
            runner.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Length,
                "and no temporary file remains");
        }
        finally
        {
            DeleteAll(path);
            if (File.Exists(blocker)) File.Delete(blocker);
        }

        return Task.CompletedTask;
    });

    // The rule cap is only a bound if it stops the read: a file of empty rules just under the byte cap
    // holds millions of them, and each used to be allocated (with a generated id) before the count
    // was looked at.
    private static Task RunRuleCountDuringReadAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: the rule cap stops the read", () =>
    {
        var path = TempPath();
        try
        {
            var head = Encoding.UTF8.GetBytes("{\"Rules\":[");
            var tail = Encoding.UTF8.GetBytes("]}");
            var bytes = new byte[AutoResponderSettingsStore.MaxFileBytes - 16];
            head.CopyTo(bytes, 0);
            tail.CopyTo(bytes, bytes.Length - tail.Length);
            Array.Fill(bytes, (byte)' ', head.Length, bytes.Length - head.Length - tail.Length);
            var commaCount = (bytes.Length - head.Length - tail.Length - 2) / 3;
            var at = head.Length;
            for (var i = 0; i < commaCount; i++)
            {
                bytes[at++] = (byte)'{';
                bytes[at++] = (byte)'}';
                bytes[at++] = (byte)',';
            }

            bytes[at++] = (byte)'{';
            bytes[at] = (byte)'}';
            File.WriteAllBytes(path, bytes);

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var (result, elapsed) = Timed(() => AutoResponderSettingsStore.Load(path));
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            runner.AreEqual(AutoResponderLoadStatus.TooManyRules, result?.Status ?? AutoResponderLoadStatus.Loaded,
                "a file just under the byte cap full of {} is TooManyRules");
            runner.IsTrue(result?.Settings is null, "and none of it is used");
            runner.IsTrue(elapsed < TimeSpan.FromSeconds(5), $"quickly ({elapsed.TotalMilliseconds:N0} ms)");

            // Timed runs on another thread, so measure the same load here for its allocations.
            allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            AutoResponderSettingsStore.Load(path);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            runner.IsTrue(allocated < 100L * 1024 * 1024,
                $"without building millions of rules ({allocated / (1024 * 1024):N0} MB allocated)");
        }
        finally
        {
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    // A hard kill between writing the temporary file and moving it leaves the temporary behind, and
    // nothing else ever removes it.
    private static Task RunStaleTemporariesAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: stale temporaries are swept", () =>
    {
        var path = TempPath();
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var stale = $"{path}.{Guid.NewGuid():N}.tmp";
        var fresh = $"{path}.{Guid.NewGuid():N}.tmp";
        var otherRules = Path.Combine(directory, $"{name}.notaguid.tmp");
        var unrelated = Path.Combine(directory, $"{name}.keep");
        var otherFile = Path.Combine(directory, $"other-{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            foreach (var file in new[] { stale, fresh, otherRules, unrelated, otherFile }) File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
            File.SetLastWriteTimeUtc(otherRules, DateTime.UtcNow.AddDays(-1));
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-1));
            File.SetLastWriteTimeUtc(otherFile, DateTime.UtcNow.AddDays(-1));

            // An explicit path is an export into a folder the user chose: nothing there is swept.
            runner.IsTrue(AutoResponderSettingsStore.Save(new AutoResponderSettings(), path).Succeeded, "saved");
            runner.IsTrue(File.Exists(stale), "a stale temporary beside an export is left alone");

            // Odd names the wildcard also matches (empty, short, and one character over the shape)
            // must be skipped, not abort the sweep.
            var odd = new[] { $"{path}..tmp", $"{path}.x.tmp", $"{path}.{new string('a', 33)}.tmp" };
            foreach (var file in odd)
            {
                File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));
            }

            AutoResponderSettingsStore.SweepStaleTemporaries(path);
            foreach (var file in odd)
            {
                runner.IsTrue(File.Exists(file), "an odd-shaped name is left alone");
                File.Delete(file);
            }

            runner.IsTrue(!File.Exists(stale), "an old temporary from a killed save is removed");
            runner.IsTrue(File.Exists(fresh), "a recent one may belong to a save in progress and stays");
            runner.IsTrue(File.Exists(otherRules), "a file that only looks like one stays");
            runner.IsTrue(File.Exists(unrelated), "an unrelated sibling stays");
            runner.IsTrue(File.Exists(otherFile), "another file's temporary stays");
        }
        finally
        {
            DeleteAll(path);
            foreach (var file in new[] { stale, fresh, otherRules, unrelated, otherFile })
                if (File.Exists(file)) File.Delete(file);
        }

        return Task.CompletedTask;
    });

    // What the user reads about a bad file must come from the catalogue, never from the raw key or
    // from wording the code wrote itself.
    private static Task RunUserFacingTextAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: user-facing text", () =>
    {
        var path = TempPath();
        try
        {
            var tooMany = Piper.App.Strings.AutoResponder.ImportTooManyRules(5001);
            runner.IsTrue(!tooMany.StartsWith("autoResponder.", StringComparison.Ordinal), "the too-many message is not a raw key");
            runner.IsTrue(tooMany.Contains(AutoResponderSettingsStore.MaxRules.ToString("N0")), "and names the limit");
            runner.IsTrue(tooMany.Contains(" rules would"), "several rules are plural");
            runner.IsTrue(tooMany.Contains($"{AutoResponderSettingsStore.MaxRules:N0} rules Piper allows"),
                "and the limit is stated as a number of rules");

            var one = Piper.App.Strings.AutoResponder.ImportTooManyRules(1);
            runner.IsTrue(!one.StartsWith("autoResponder.", StringComparison.Ordinal), "the singular message is not a raw key");
            runner.IsTrue(one.Contains("1 rule would") && !one.Contains("rules would"), "one rule is singular");

            var tooLarge = Piper.App.Strings.AutoResponder.ImportTooLarge;
            runner.IsTrue(!tooLarge.StartsWith("autoResponder.", StringComparison.Ordinal), "the too-large message is not a raw key");
            runner.IsTrue(tooLarge.Contains((AutoResponderSettingsStore.MaxFileBytes / (1024 * 1024)).ToString("N0")), "and names the size limit");

            runner.AreEqual(Piper.App.Strings.AutoResponder.UnreadableRuleSet,
                Piper.App.Strings.AutoResponder.LoadProblem(new AutoResponderLoadResult(AutoResponderLoadStatus.Unreadable)),
                "an unreadable file without a detail falls back to the generic sentence");

            foreach (var (text, what) in new[] { ("null", "a null document"), ("""{"Rules":5}""", "Rules that is not an array") })
            {
                var result = Load(path, text);
                runner.AreEqual(AutoResponderLoadStatus.Malformed, result.Status, $"{what} is malformed");
                runner.IsTrue(result.Detail is null, $"{what} carries no author-written detail");
                runner.AreEqual(Piper.App.Strings.AutoResponder.UnreadableRuleSet, Piper.App.Strings.AutoResponder.LoadProblem(result),
                    $"{what} is explained by the catalogue's generic sentence");
            }

            var truncated = Load(path, """{"Rules":[""");
            runner.IsTrue(truncated.Detail is not null, "a parser error keeps the parser's own detail");

            // System messages can embed whatever the caller or the file supplied, and the detail goes
            // to the log and a dialog: it is cut short whatever its source.
            var farTooLong = Path.Combine(Path.GetTempPath(), new string('p', 40_000), "rules.json");
            var unusable = AutoResponderSettingsStore.Load(farTooLong);
            runner.AreEqual(AutoResponderLoadStatus.Unreadable, unusable.Status, "an impossible path is unreadable");
            runner.IsTrue(unusable.Detail is { Length: > 0 and <= 210 }, $"and its detail stays short ({unusable.Detail?.Length} characters)");

            var syntax = Load(path, "{\"Rules\":[{\"" + new string('n', 1_000_000) + "\":tru}]}");
            runner.AreEqual(AutoResponderLoadStatus.Malformed, syntax.Status, "a syntax error under a huge property name is malformed");
            runner.IsTrue(syntax.Detail is { Length: > 0 and <= 210 }, $"and its detail stays short ({syntax.Detail?.Length} characters)");
        }
        finally
        {
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    // Two sets that each save fine can add up to one that Save refuses, and once such a set is applied
    // every later edit fails to persist. The import checks the merged set first.
    private static Task RunAppendLimitsAsync(TestRunner runner) => runner.RunAsync("AutoResponder import: append checks the limits Save enforces", () =>
    {
        static AutoResponderSettings WithBody(int bytes) => new()
        {
            Rules = [new AutoResponderRule { Match = "/x", Action = "*inline", Body = new string('b', bytes) }],
        };

        var current = WithBody(5 * 1024 * 1024);
        var imported = WithBody(5 * 1024 * 1024);
        runner.AreEqual(AutoResponderSaveStatus.Saved, AutoResponderSettingsStore.CheckLimits(current), "each set is within the limits");
        runner.AreEqual(AutoResponderSaveStatus.Saved, AutoResponderSettingsStore.CheckLimits(imported), "on its own");
        runner.AreEqual(AutoResponderSaveStatus.TooLarge, AutoResponderSettingsStore.CheckLimits(current.Appended(imported)),
            "but two 5 MB sets together are too large to save");

        var many = new AutoResponderSettings
        {
            Rules = [.. Enumerable.Range(0, AutoResponderSettingsStore.MaxRules).Select(_ => new AutoResponderRule())],
        };
        runner.AreEqual(AutoResponderSaveStatus.Saved, AutoResponderSettingsStore.CheckLimits(many), "a set at the rule cap is fine");
        runner.AreEqual(AutoResponderSaveStatus.TooManyRules,
            AutoResponderSettingsStore.CheckLimits(many.Appended(new AutoResponderSettings { Rules = [new AutoResponderRule()] })),
            "one more rule is over it");

        // Load bounds the file as written; Save bounds the indented form it writes back. A compact
        // file just under the byte cap therefore loads, and must still be refused on import (Replace
        // and an empty list apply it as is), or the first autosave would fail and stay failed.
        var path = TempPath();
        try
        {
            var bodyBytes = (int)((AutoResponderSettingsStore.MaxFileBytes - 150_000) / AutoResponderSettingsStore.MaxRules) - 130;
            var compact = new AutoResponderSettings
            {
                Rules = [.. Enumerable.Range(0, AutoResponderSettingsStore.MaxRules)
                    .Select(i => new AutoResponderRule { Match = $"/r{i}", Action = "*inline", Body = new string('b', bodyBytes) })],
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(compact));
            runner.IsTrue(new FileInfo(path).Length <= AutoResponderSettingsStore.MaxFileBytes, "the compact file is under the byte cap");

            var loaded = AutoResponderSettingsStore.Load(path);
            runner.AreEqual(AutoResponderLoadStatus.Loaded, loaded.Status, "so it loads");
            runner.AreEqual(AutoResponderSaveStatus.TooLarge, AutoResponderSettingsStore.CheckLimits(loaded.Settings!),
                "but its indented form is over the cap, so the import is refused");
            runner.AreEqual(AutoResponderSaveStatus.TooLarge, AutoResponderSettingsStore.Save(loaded.Settings!, path + ".out").Status,
                "which is what an autosave would have hit");
        }
        finally
        {
            DeleteAll(path);
            if (File.Exists(path + ".out")) File.Delete(path + ".out");
        }

        return Task.CompletedTask;
    });

    // The sweep enumerates a folder on the UI thread; a process needs it once, not per edit.
    private static Task RunSweepOnceAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: the stale-temporary sweep runs once", () =>
    {
        var path = TempPath();
        var first = $"{path}.{Guid.NewGuid():N}.tmp";
        var second = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(first, "x");
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddDays(-1));

            var gate = 0;
            runner.IsTrue(AutoResponderSettingsStore.SweepOnce(path, ref gate), "the first call sweeps");
            runner.IsTrue(!File.Exists(first), "removing the stale temporary");

            File.WriteAllText(second, "x");
            File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddDays(-1));
            runner.IsTrue(!AutoResponderSettingsStore.SweepOnce(path, ref gate), "a later call does nothing");
            runner.IsTrue(File.Exists(second), "and enumerates nothing");
        }
        finally
        {
            foreach (var file in new[] { first, second }) if (File.Exists(file)) File.Delete(file);
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    // What the launch path does with each kind of unusable file is decided by these flags, so they are
    // pinned here: an over-limit rule set is real work and is never moved or overwritten.
    private static Task RunFileProblemPolicyAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: what is set aside and what is protected", () =>
    {
        var path = TempPath();
        try
        {
            var malformed = Load(path, "{ broken");
            runner.IsTrue(malformed.SetAsideAdvised && !malformed.OverLimits, "a file that is not a rule set is set aside");

            File.WriteAllText(path, "{\"Rules\":[" + string.Join(",", Enumerable.Repeat("{}", AutoResponderSettingsStore.MaxRules + 1)) + "]}");
            var tooMany = AutoResponderSettingsStore.Load(path);
            runner.IsTrue(tooMany.OverLimits && !tooMany.SetAsideAdvised, "a valid set with too many rules is protected, not moved");

            File.WriteAllBytes(path, new byte[AutoResponderSettingsStore.MaxFileBytes + 1]);
            var tooLarge = AutoResponderSettingsStore.Load(path);
            runner.IsTrue(tooLarge.OverLimits && !tooLarge.SetAsideAdvised, "an oversized file is protected, not moved");

            var unreadable = AutoResponderSettingsStore.Load(Path.GetTempPath());
            runner.IsTrue(!unreadable.OverLimits && !unreadable.SetAsideAdvised, "a file that cannot be opened is neither");

            var loaded = Load(path, "{}");
            runner.IsTrue(!loaded.OverLimits && !loaded.SetAsideAdvised, "a good file is neither");

            // A save failure's detail is bounded like a load failure's: it embeds the path.
            var overlong = AutoResponderSettingsStore.Save(new AutoResponderSettings(),
                Path.Combine(Path.GetTempPath(), new string('p', 40_000), "rules.json"));
            runner.AreEqual(AutoResponderSaveStatus.Failed, overlong.Status, "an impossible save path fails");
            runner.IsTrue(overlong.Detail is { Length: > 0 and <= 210 }, $"with a short detail ({overlong.Detail?.Length} characters)");

            // In the background, as the store runs it from the UI thread.
            var stale = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(stale, "x");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
            var gate = 0;
            runner.IsTrue(AutoResponderSettingsStore.SweepOnce(path, ref gate, inBackground: true), "the first call starts a sweep");
            var deadline = Environment.TickCount64 + 10_000;
            while (File.Exists(stale) && Environment.TickCount64 < deadline) Thread.Sleep(20);
            runner.IsTrue(!File.Exists(stale), "which removes the stale temporary without the caller waiting");
            runner.IsTrue(!AutoResponderSettingsStore.SweepOnce(path, ref gate, inBackground: true), "and only once");
        }
        finally
        {
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    private static Task RunSetAsideAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules file: an unusable file is set aside", () =>
    {
        var path = TempPath();
        try
        {
            runner.AreEqual<string?>(null, AutoResponderSettingsStore.SetAside(path), "no file, nothing to set aside");

            File.WriteAllText(path, "{ hand written, and broken");
            var moved = AutoResponderSettingsStore.SetAside(path);
            runner.AreEqual(path + ".invalid", moved, "the file is renamed beside itself");
            runner.IsTrue(!File.Exists(path), "the original name is free for the next save");
            runner.AreEqual("{ hand written, and broken", File.ReadAllText(path + ".invalid"), "and its content is kept");

            // An earlier copy is never overwritten while a numbered slot is free.
            File.WriteAllText(path, "second breakage");
            runner.AreEqual(path + ".invalid.1", AutoResponderSettingsStore.SetAside(path), "a second one takes the next slot");
            runner.AreEqual("second breakage", File.ReadAllText(path + ".invalid.1"), "with its own content");
            runner.AreEqual("{ hand written, and broken", File.ReadAllText(path + ".invalid"), "and the first is untouched");

            for (var i = 2; i < AutoResponderSettingsStore.SetAsideSlots; i++)
            {
                File.WriteAllText(path, $"breakage {i}");
                runner.AreEqual($"{path}.invalid.{i}", AutoResponderSettingsStore.SetAside(path), $"slot {i} is used in turn");
            }

            // Every slot taken: the oldest is replaced, and nothing else is disturbed.
            File.SetLastWriteTimeUtc(path + ".invalid.3", DateTime.UtcNow.AddDays(-30));
            File.WriteAllText(path, "one too many");
            runner.AreEqual(path + ".invalid.3", AutoResponderSettingsStore.SetAside(path), "with all slots taken the oldest is replaced");
            runner.AreEqual("one too many", File.ReadAllText(path + ".invalid.3"), "by the newest");
            runner.AreEqual("{ hand written, and broken", File.ReadAllText(path + ".invalid"), "and the first is still there");
            runner.IsTrue(!File.Exists($"{path}.invalid.{AutoResponderSettingsStore.SetAsideSlots}"), "no more than the bounded number of copies exist");
        }
        finally
        {
            DeleteAll(path);
        }

        return Task.CompletedTask;
    });

    private static Task RunAppendAsync(TestRunner runner) => runner.RunAsync("AutoResponder import: append keeps existing rules", () =>
    {
        var current = new AutoResponderSettings
        {
            Enabled = true,
            PassthroughUnmatched = false,
            Rules =
            [
                new AutoResponderRule { Id = "one", Match = "/one", Action = "*404" },
                new AutoResponderRule { Id = "two", Match = "/two", Action = "*500" },
            ],
        };
        var imported = new AutoResponderSettings
        {
            Enabled = false,
            PassthroughUnmatched = true,
            Rules =
            [
                new AutoResponderRule { Id = "two", Match = "/imported-two", Action = "*418" },
                new AutoResponderRule { Id = "three", Match = "/three", Action = "*200" },
            ],
        };

        var merged = current.Appended(imported);
        runner.AreEqual(4, merged.Rules.Count, "both sets of rules are kept");
        runner.AreEqual("/one|/two|/imported-two|/three", string.Join("|", merged.Rules.Select(r => r.Match)),
            "existing rules keep their place and the imported ones follow in order");
        runner.AreEqual(true, merged.Enabled, "the current master toggle wins");
        runner.AreEqual(false, merged.PassthroughUnmatched, "and so does the passthrough toggle");
        runner.AreEqual(4, merged.Rules.Select(r => r.Id).Distinct().Count(), "an imported id that is already in use is replaced");
        runner.AreEqual("two", merged.Rules[1].Id, "the existing rule keeps its id, and its hit count");
        runner.AreEqual("three", merged.Rules[3].Id, "an imported id that is free is kept");
        runner.AreEqual(2, current.Rules.Count, "the current set is not modified");
        runner.AreEqual("two", imported.Rules[0].Id, "nor is the imported one");
        runner.IsTrue(!ReferenceEquals(merged.Rules[2], imported.Rules[0]), "imported rules are copies");

        runner.AreEqual(2, current.Appended(new AutoResponderSettings()).Rules.Count, "appending nothing changes nothing");
        return Task.CompletedTask;
    });

    // A rules file is loaded when the application starts, so a rule that takes quadratic time to
    // parse, or overflows while doing it, is a hang or a crash at every launch.
    private static Task RunHostileExpressionsAsync(TestRunner runner) => runner.RunAsync("AutoResponder expressions: hostile repetition", () =>
    {
        var capped = AutoResponderAction.Parse("*delay:2147483647 *delay:2147483647 *delay:2147483647 *404");
        runner.AreEqual(TimeSpan.FromMilliseconds(AutoResponderAction.MaxDelayMilliseconds), capped.Delay,
            "stacked delays are clamped, not passed to Task.Delay to throw");
        runner.AreEqual(AutoResponderOutcome.Respond, capped.Outcome, "and the action behind them still parses");
        runner.AreEqual(TimeSpan.FromMilliseconds(300), AutoResponderAction.Parse("*delay:100 *delay:200;*503").Delay,
            "delays below the clamp still add up");

        var delays = string.Concat(Enumerable.Repeat("*delay:2147483647 ", 400_000)) + "*404";
        var (action, delayTime) = Timed(() => AutoResponderAction.Parse(delays));
        runner.IsTrue(action is not null && action.Delay.TotalMilliseconds == AutoResponderAction.MaxDelayMilliseconds,
            "hundreds of thousands of delay prefixes parse, clamped");
        runner.IsTrue(delayTime < TimeSpan.FromSeconds(10), $"in linear time ({delayTime.TotalMilliseconds:N0} ms)");

        var negations = string.Concat(Enumerable.Repeat("NOT:", 1_000_000)) + "orders";
        var (match, negationTime) = Timed(() => AutoResponderMatch.Parse(negations));
        runner.IsTrue(match is not null && match.Warning is null, "a million NOT: prefixes parse");
        runner.IsTrue(negationTime < TimeSpan.FromSeconds(10), $"in linear time ({negationTime.TotalMilliseconds:N0} ms)");

        return Task.CompletedTask;
    });

    /// <summary>
    /// Runs <paramref name="work"/> on a worker with a ceiling, so a regression fails this test
    /// rather than hanging the whole run. The worker is abandoned on timeout; it is a background thread.
    /// </summary>
    private static (T? Result, TimeSpan Elapsed) Timed<T>(Func<T> work) where T : class
    {
        var clock = Stopwatch.StartNew();
        var task = Task.Run(work);
        return task.Wait(TimeSpan.FromSeconds(30)) ? (task.Result, clock.Elapsed) : (null, clock.Elapsed);
    }

    private static AutoResponderLoadResult Load(string path, string content)
    {
        File.WriteAllText(path, content);
        return AutoResponderSettingsStore.Load(path);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"piper-autoresponder-rules-{Guid.NewGuid():N}.json");

    private static void DeleteAll(string path)
    {
        foreach (var file in new[] { path, path + ".invalid" }.Concat(Enumerable.Range(1, AutoResponderSettingsStore.SetAsideSlots).Select(i => $"{path}.invalid.{i}")))
        {
            if (!File.Exists(file)) continue;
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        var directory = Path.GetDirectoryName(path)!;
        foreach (var leftover in Directory.GetFiles(directory, Path.GetFileName(path) + ".*.tmp"))
            File.Delete(leftover);
    }
}
