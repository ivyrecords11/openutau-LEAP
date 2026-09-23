using System.Text;
using System.Text.RegularExpressions;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;

namespace LEAP;

[Phonemizer("LEAP", "MULTI LEAP", "iv=p", language: "MULTI")]
public sealed class LEAPhonemizer : Phonemizer {
    private enum LyricLanguage { Unknown, Korean, Japanese, English }
    private int ticksPerBeat = 480;

    // Keep one initialized delegate set for the lifetime of this phonemizer.
    // Creating delegates per render thread makes their async dictionaries finish
    // repeatedly, which invalidates phonemes and defeats the resampler cache.
    // The lock protects the delegates' mutable state from concurrent render jobs.
    private readonly object sessionLock = new();
    private readonly RouterSession session = new();

    public override void SetSinger(USinger singer) {
        lock (sessionLock) {
            session.SetSinger(singer);
        }
    }

    public override void SetUp(Note[][] notes, UProject project, UTrack track) {
        base.SetUp(notes, project, track);
        ticksPerBeat = GetTicksPerBeat(project);
        lock (sessionLock) {
            foreach (var phonemizer in session.Delegates()) {
                phonemizer.SetTiming(timeAxis);
                phonemizer.SetUp(notes, project, track);
            }
        }
    }

    private static int GetTicksPerBeat(UProject project) {
        // OpenUtau exposes resolution as a field; UtauV exposes it as a property.
        // Avoid a compiled field or getter reference that fails in the other host.
        var type = project.GetType();
        object? resolution = type.GetField("resolution")?.GetValue(project)
            ?? type.GetProperty("resolution")?.GetValue(project);
        if (resolution is int ticks && ticks > 0) {
            return ticks;
        }
        throw new NotSupportedException("This OpenUtau host does not expose a valid project resolution.");
    }

    public override Result Process(
        Note[] notes,
        Note? prev,
        Note? next,
        Note? prevNeighbour,
        Note? nextNeighbour,
        Note[] prevNeighbours) {
        lock (sessionLock) {
            var language = ResolveLanguage(notes[0], prevNeighbour, nextNeighbour);
            var phonemizer = session.Select(language);
            if (language == LyricLanguage.English) {
                session.SetEnglishColor(notes[0].phonemeAttributes?.FirstOrDefault(a => a.index == 0).voiceColor
                    ?? "");
            }
            var filteredPrev = SameLanguage(prev, language);
            var filteredNext = SameLanguage(next, language);
            var filteredPrevNeighbour = SameLanguage(prevNeighbour, language);
            var filteredNextNeighbour = SameLanguage(nextNeighbour, language);
            var filteredPrevs = prevNeighbours.Where(note => SameLanguage(note, language).HasValue).ToArray();
            var result = phonemizer.Process(
                notes,
                filteredPrev,
                filteredNext,
                filteredPrevNeighbour,
                filteredNextNeighbour,
                filteredPrevs);
            if (language == LyricLanguage.Korean) {
                return ApplyShortBatchimTiming(notes, result, filteredNextNeighbour);
            }
            if (language == LyricLanguage.English) {
                return ApplyEnglishAliasMapping(notes, result);
            }
            return result;
        }
    }

    private Result ApplyEnglishAliasMapping(Note[] notes, Result result) {
        if (result.phonemes == null || result.phonemes.Length == 0) {
            return result;
        }
        var attributes = notes[0].phonemeAttributes ?? Array.Empty<PhonemeAttributes>();
        for (int i = 0; i < result.phonemes.Length; ++i) {
            var phoneme = result.phonemes[i];
            int attributeIndex = phoneme.index ?? i;
            var attribute = attributes.FirstOrDefault(attr => attr.index == attributeIndex);
            // English aliases use fixed recording pitches, so no tone shift is
            // needed here. Referencing toneShift also breaks hosts whose API
            // exposes a different field signature (MissingFieldException).
            string color = attribute.voiceColor ?? "";
            phoneme.phoneme = session.ResolveEnglishAlias(phoneme.phoneme, color);
            result.phonemes[i] = phoneme;
        }
        return result;
    }

    private Result ApplyShortBatchimTiming(Note[] notes, Result result, Note? nextNeighbour) {
        int totalDuration = notes.Sum(note => note.duration);
        if (totalDuration <= 0 ||
            totalDuration > ticksPerBeat ||
            !HasHangulBatchim(notes[0].lyric) ||
            FlowsBatchimIntoNextSyllable(notes[0].lyric, nextNeighbour?.lyric) ||
            result.phonemes is not { Length: 2 }) {
            return result;
        }

        // Korean CVVC returns [CV, vowel-final]. For notes up to one beat,
        // place the vowel-to-batchim boundary at exactly half the note.
        var batchim = result.phonemes[1];
        batchim.position = totalDuration / 2;
        result.phonemes[1] = batchim;
        return result;
    }

    private static bool FlowsBatchimIntoNextSyllable(
        string? currentLyric, string? nextLyric) {
        if (!HasHangulBatchim(currentLyric) || string.IsNullOrWhiteSpace(nextLyric)) {
            return false;
        }

        // A composed Hangul syllable whose initial index is 11 begins with ㅇ.
        // At an immediate boundary this is a silent onset, so the preceding
        // batchim is handled by Korean liaison. Preserve Korean CVVC's timing.
        foreach (var rune in nextLyric.EnumerateRunes()) {
            if (rune.Value is >= 0xAC00 and <= 0xD7A3) {
                int initial = (rune.Value - 0xAC00) / (21 * 28);
                return initial == 11;
            }
        }
        return false;
    }

    private static bool HasHangulBatchim(string? lyric) {
        if (string.IsNullOrWhiteSpace(lyric)) {
            return false;
        }
        foreach (var rune in lyric.EnumerateRunes()) {
            if (rune.Value is >= 0xAC00 and <= 0xD7A3 &&
                (rune.Value - 0xAC00) % 28 != 0) {
                return true;
            }
        }
        return false;
    }

    public override void CleanUp() {
        lock (sessionLock) {
            foreach (var phonemizer in session.Delegates()) {
                phonemizer.CleanUp();
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyAliasMap =
        new Dictionary<string, string>();

    private static readonly IReadOnlyDictionary<string, string[]> EnglishFallbacks =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
            ["ax"] = new[] { "ah" },
            ["axr"] = new[] { "er" },
            ["ae"] = new[] { "ah", "eh" },
            ["aa"] = new[] { "ah" },
            ["iy"] = new[] { "ih" },
            ["uh"] = new[] { "uw" },
            ["ix"] = new[] { "ih" },
            ["ux"] = new[] { "uw" },
            ["oh"] = new[] { "ao" },
            ["nx"] = new[] { "n" },
            ["tx"] = new[] { "t" },
            ["dx"] = new[] { "d", "t" },
            ["ng"] = new[] { "n" },
            ["zh"] = new[] { "sh", "z" },
            ["dh"] = new[] { "d" },
            ["th"] = new[] { "s" },
            ["z"] = new[] { "s" },
            ["cl"] = new[] { "q" },
            ["vf"] = new[] { "q" },
            ["dd"] = new[] { "d" },
            ["lx"] = new[] { "l" },
            ["el"] = new[] { "l" },
            ["em"] = new[] { "m" },
            ["en"] = new[] { "n" },
        };

    private static IEnumerable<string> EnglishAliasCandidates(string phoneme) {
        string normalized = Regex.Replace(phoneme.Trim(), @"\s+", " ");
        if (normalized.Length == 0) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(normalized)) yield return normalized;

        string[] tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<string> { string.Empty };
        foreach (string token in tokens) {
            var variants = new List<string> { token };
            if (EnglishFallbacks.TryGetValue(token, out var replacements)) {
                variants.AddRange(replacements);
            }
            candidates = candidates
                .SelectMany(prefix => variants.Select(variant =>
                    prefix.Length == 0 ? variant : $"{prefix} {variant}"))
                .ToList();
        }
        foreach (string candidate in candidates.Skip(1)) {
            if (seen.Add(candidate)) yield return candidate;
        }

        // Arpasing uses an explicit rest marker for note-initial vowels.
        if (tokens.Length == 1 && IsEnglishVowel(tokens[0])) {
            foreach (string candidate in candidates) {
                string initial = $"- {candidate}";
                if (seen.Add(initial)) yield return initial;
            }
        }
    }

    private static bool IsEnglishVowel(string symbol) =>
        symbol is "aa" or "ae" or "ah" or "ao" or "aw" or "ax" or "axr" or
        "ay" or "eh" or "er" or "ey" or "ih" or "ix" or "iy" or "oh" or
        "ow" or "oy" or "uh" or "uw" or "ux";

    private sealed class RouterSession {
        public KoreanCVVCPhonemizer Korean { get; } = new();
        public JapanesePresampPhonemizer Japanese { get; } = new();
        public ArpasingPlusPhonemizer English { get; } = new();
        private USinger? singer;
        private bool singerLoaded;
        private AliasMapSinger? englishSinger;

        public void SetEnglishColor(string? color) {
            if (englishSinger != null) englishSinger.EnglishColor = color ?? "";
        }

        public void SetSinger(USinger singer) {
            // SyllableBasedPhonemizer compares singer objects by reference and
            // starts asynchronous dictionary loading whenever that reference
            // changes. Reusing these wrappers prevents an init/render loop.
            if (ReferenceEquals(this.singer, singer) && singerLoaded == singer.Loaded) {
                return;
            }
            this.singer = singer;
            singerLoaded = singer.Loaded;
            var maps = AliasMaps.Load(singer.Location);
            Korean.SetSinger(new AliasMapSinger(singer, maps.Korean));
            Japanese.SetSinger(new AliasMapSinger(singer, maps.Japanese));
            englishSinger = new AliasMapSinger(singer, EmptyAliasMap, fixedEnglishPitch: true);
            English.SetSinger(englishSinger);
        }

        public IEnumerable<Phonemizer> Delegates() {
            yield return Korean;
            yield return Japanese;
            yield return English;
        }

        public Phonemizer Select(LyricLanguage language) => language switch {
            LyricLanguage.Korean => Korean,
            LyricLanguage.Japanese => Japanese,
            _ => English,
        };

        public string ResolveEnglishAlias(string phoneme, string? color) {
            if (singer == null || string.IsNullOrWhiteSpace(phoneme)) {
                return phoneme;
            }
            if (singer.TryGetOto(phoneme, out var alreadyMapped)) {
                return alreadyMapped.Alias;
            }

            bool flow = string.Equals(color?.Trim(), "Flow", StringComparison.OrdinalIgnoreCase);
            foreach (string candidate in EnglishAliasCandidates(phoneme)) {
                if (flow && singer.TryGetOto($"Flow{candidate}A3", out var flowOto)) {
                    return flowOto.Alias;
                }
                if (!flow && singer.TryGetOto($"Stable{candidate}G4", out var stableOto)) {
                    return stableOto.Alias;
                }
            }

            // Prefer a stable recording to a red/missing phoneme when the Flow
            // bank lacks a particular consonant or transition.
            if (flow) {
                foreach (string candidate in EnglishAliasCandidates(phoneme)) {
                    if (singer.TryGetOto($"Stable{candidate}G4", out var stableOto)) {
                        return stableOto.Alias;
                    }
                }
            }
            return phoneme;
        }
    }

    private static Note? SameLanguage(Note? note, LyricLanguage language) {
        if (!note.HasValue) {
            return null;
        }
        return Detect(note.Value.lyric) == language || note.Value.lyric?.StartsWith("+") == true
            ? note : null;
    }

    private static LyricLanguage ResolveLanguage(Note note, Note? prev, Note? next) {
        var language = Detect(note.lyric);
        if (language != LyricLanguage.Unknown) {
            return language;
        }
        if (prev.HasValue && Detect(prev.Value.lyric) is var previous && previous != LyricLanguage.Unknown) {
            return previous;
        }
        if (next.HasValue && Detect(next.Value.lyric) is var following && following != LyricLanguage.Unknown) {
            return following;
        }
        return LyricLanguage.English;
    }

    private static LyricLanguage Detect(string? lyric) {
        if (string.IsNullOrWhiteSpace(lyric)) {
            return LyricLanguage.Unknown;
        }
        foreach (var rune in lyric.EnumerateRunes()) {
            int value = rune.Value;
            if ((value >= 0xAC00 && value <= 0xD7A3) ||
                (value >= 0x1100 && value <= 0x11FF) ||
                (value >= 0x3130 && value <= 0x318F)) {
                return LyricLanguage.Korean;
            }
            if ((value >= 0x3040 && value <= 0x30FF) ||
                (value >= 0x31F0 && value <= 0x31FF)) {
                return LyricLanguage.Japanese;
            }
        }
        return lyric.Any(char.IsLetter) ? LyricLanguage.English : LyricLanguage.Unknown;
    }
}

internal sealed class AliasMaps {
    public Dictionary<string, string> Korean { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Japanese { get; } = new(StringComparer.Ordinal);

    public static AliasMaps Load(string singerLocation) {
        var result = new AliasMaps();
        string path = Path.Combine(singerLocation, "iv_legacy_aliases.tsv");
        if (!File.Exists(path)) {
            // The separate KR/JP/EN releases intentionally use the stock
            // phonemizers and do not ship this IV-specific map. A project can
            // still retain this phonemizer from a previous singer, however;
            // in that case use native aliases instead of aborting the worker.
            return result;
        }
        foreach (string line in File.ReadLines(path, Encoding.UTF8).Skip(1)) {
            string[] parts = line.Split('\t');
            if (parts.Length != 3) {
                continue;
            }
            var target = parts[0] switch {
                "ko" => result.Korean,
                "ja" => result.Japanese,
                _ => null,
            };
            if (target is not null) {
                target[parts[1]] = parts[2];
            }
        }
        return result;
    }
}

internal sealed class AliasMapSinger : USinger {
    private const string DefaultColor = "";
    private const string AlternateColor = "Flow";
    private static readonly Regex AlternateSuffix = new("^(.*?)([0-9]+)$", RegexOptions.Compiled);
    private readonly USinger inner;
    private readonly IReadOnlyDictionary<string, string> aliases;
    private readonly HashSet<string> nativeAliases;
    private readonly bool fixedEnglishPitch;
    private readonly bool restrictToMappedLanguage;
    public string EnglishColor { get; set; } = "";

    public AliasMapSinger(
        USinger inner,
        IReadOnlyDictionary<string, string> aliases,
        bool fixedEnglishPitch = false) {
        this.inner = inner;
        this.aliases = aliases;
        nativeAliases = aliases.Values.ToHashSet(StringComparer.Ordinal);
        this.fixedEnglishPitch = fixedEnglishPitch;
        restrictToMappedLanguage = !fixedEnglishPitch && aliases.Count > 0;
        found = inner.Found;
        loaded = inner.Loaded;
    }

    public override string Id => inner.Id;
    public override string Name => inner.Name;
    public override USingerType SingerType => inner.SingerType;
    public override string BasePath => inner.BasePath;
    public override string Location => inner.Location;
    public override Encoding TextFileEncoding => inner.TextFileEncoding;
    public override IList<USubbank> Subbanks => inner.Subbanks;
    public override IList<UOto> Otos => inner.Otos;

    public override bool TryGetOto(string phoneme, out UOto oto) {
        if (!TryTranslate(phoneme, out string translated)) {
            oto = null!;
            return false;
        }
        return inner.TryGetOto(translated, out oto);
    }

    public override bool TryGetMappedOto(string phoneme, int tone, out UOto oto) =>
        TryGetMappedOto(phoneme, tone, fixedEnglishPitch ? EnglishColor : DefaultColor, out oto);

    public override bool TryGetMappedOto(string phoneme, int tone, string color, out UOto oto) {
        if (!TryTranslate(phoneme, out string translated)) {
            oto = null!;
            return false;
        }
        string normalizedColor = NormalizeColor(color);
        if (fixedEnglishPitch) {
            // ARPA+ also rechecks aliases after affixes have already been applied.
            if ((translated.StartsWith("Stable", StringComparison.Ordinal) ||
                 translated.StartsWith("Flow", StringComparison.Ordinal)) &&
                inner.TryGetOto(translated, out oto)) return true;
            string prefix = normalizedColor == AlternateColor ? AlternateColor : "Stable";
            // Both English colors are monopitch: Stable is recorded at G4 and
            // Flow is recorded at A3. Bypass the shared JA/KO pitch ladder.
            string suffix = normalizedColor == AlternateColor ? "A3" : "G4";
            return inner.TryGetOto($"{prefix}{translated}{suffix}", out oto);
        }
        return inner.TryGetMappedOto(translated, tone, normalizedColor, out oto);
    }

    // Stable is represented by the uncolored base layer. Flow is opt-in only.
    private static string NormalizeColor(string? color) =>
        string.Equals(color?.Trim(), AlternateColor, StringComparison.OrdinalIgnoreCase)
            ? AlternateColor
            : DefaultColor;

    private bool TryTranslate(string phoneme, out string translated) {
        if (aliases.TryGetValue(phoneme, out string? mapped)) {
            translated = mapped;
            return true;
        }
        Match match = AlternateSuffix.Match(phoneme);
        if (match.Success && aliases.TryGetValue(match.Groups[1].Value, out translated)) {
            translated += match.Groups[2].Value;
            return true;
        }
        translated = phoneme;
        // Korean/Japanese wrappers may accept an already-native alias, but an
        // unmapped romanized working symbol must never leak into another
        // language's OTO set. English uses its dedicated unrestricted wrapper.
        return !restrictToMappedLanguage || nativeAliases.Contains(phoneme);
    }
}
