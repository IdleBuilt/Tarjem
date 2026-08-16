namespace Tarjem.Core.Language;

/// <summary>
/// Repairs the token OCR produced for the word under the cursor.
///
/// The two failures this exists to stop are opposite and both bad. OCR <b>splits or merges</b>
/// words at tight kerning, so the token under the cursor may be half a word or two words stuck
/// together. And OCR <b>substitutes look-alike glyphs</b> - rn/m, l/i/1, O/0, cl/d - producing a
/// token that is one small step from the truth. What must never happen is the third thing:
/// "correcting" a word that was read perfectly well into a different, wrong word, because the
/// user then gets a confident translation of something they never looked at.
///
/// So the policy throughout is: prefer the raw token, only move off it on strong evidence, and
/// when two candidates are equally plausible, prefer no answer over a coin flip.
/// </summary>
public static class OcrSpellCorrector
{
    /// <summary>
    /// Glyph pairs OCR genuinely confuses, and what they cost. A substitution between two shapes
    /// that render nearly identically is much weaker evidence of a different word than an
    /// arbitrary letter swap, so it is charged a fraction of a normal edit - which is what lets
    /// "rnodern" reach "modern" without also letting "modem" reach it just as easily.
    /// </summary>
    private static readonly Dictionary<(char, char), double> ConfusionCost = BuildConfusions();

    private const double NormalEditCost = 1.0;
    private const double ConfusedGlyphCost = 0.4;

    private static Dictionary<(char, char), double> BuildConfusions()
    {
        // Each group is a set of glyphs that routinely read as one another at screen resolution.
        string[] groups =
        [
            "il1|!", "o0q", "s5", "b6", "g9q", "z2", "a@", "cor", "vy", "uv", "nh", "mn", "ft", "ce", "kx", "jy", "dcl",
        ];

        var costs = new Dictionary<(char, char), double>();
        foreach (var group in groups)
        {
            foreach (var a in group)
            {
                foreach (var b in group)
                {
                    if (a != b) costs[(a, b)] = ConfusedGlyphCost;
                }
            }
        }
        return costs;
    }

    /// <summary>
    /// Edit distance that understands what OCR actually gets wrong: look-alike substitutions are
    /// cheap, adjacent transpositions are a single edit rather than two, and the two-glyph
    /// confusions ("rn" read as "m", "cl" read as "d") are handled as one cheap step instead of
    /// the two expensive ones a plain Levenshtein would charge.
    /// </summary>
    public static double Distance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        var d = new double[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = a[i - 1] == b[j - 1]
                    ? 0
                    : ConfusionCost.TryGetValue((a[i - 1], b[j - 1]), out var cheap) ? cheap : NormalEditCost;

                var best = Math.Min(
                    Math.Min(d[i - 1, j] + NormalEditCost, d[i, j - 1] + NormalEditCost),
                    d[i - 1, j - 1] + substitution);

                // Transposition: "hte" -> "the" is one slip, not two.
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    best = Math.Min(best, d[i - 2, j - 2] + NormalEditCost);

                // Two glyphs read as one: "rn" -> "m", "cl" -> "d", "vv" -> "w".
                if (i > 1 && IsLigatureConfusion(a[i - 2], a[i - 1], b[j - 1]))
                    best = Math.Min(best, d[i - 2, j - 1] + ConfusedGlyphCost);

                // ...and one glyph read as two, the same confusion in the other direction.
                if (j > 1 && IsLigatureConfusion(b[j - 2], b[j - 1], a[i - 1]))
                    best = Math.Min(best, d[i - 1, j - 2] + ConfusedGlyphCost);

                d[i, j] = best;
            }
        }

        return d[a.Length, b.Length];
    }

    private static bool IsLigatureConfusion(char first, char second, char single) =>
        (first, second, single) switch
        {
            ('r', 'n', 'm') => true,
            ('c', 'l', 'd') => true,
            ('v', 'v', 'w') => true,
            ('l', 'i', 'u') => true,
            ('i', 'i', 'u') => true,
            ('n', 'n', 'm') => true,
            _ => false,
        };

    /// <summary>How far a token of this length is allowed to move before the "correction" stops
    /// being a repair and starts being a different word. Short tokens get almost no latitude:
    /// at three letters, one edit already reaches dozens of unrelated words.</summary>
    public static double MaxDistanceFor(int length) => length switch
    {
        <= 2 => 0,
        <= 4 => 1.0,
        <= 6 => 1.6,
        <= 9 => 2.2,
        _ => 3.0,
    };

    /// <summary>
    /// Best correction for <paramref name="token"/>, or null to keep it as-is.
    ///
    /// <paramref name="contextWords"/> - the other words OCR read on the same line - are tried
    /// first and judged more generously, because a word that is demonstrably on screen is a far
    /// better hypothesis than one merely present in a 47,000-word list.
    /// </summary>
    public static string? Correct(string token, IEnumerable<string>? contextWords = null)
    {
        var clean = EnglishWordList.Normalize(token ?? "");
        if (clean.Length == 0) return null;

        // Read correctly already. This is the common case and it must be cheap and absolute.
        if (EnglishWordList.Contains(clean)) return null;

        var limit = MaxDistanceFor(clean.Length);
        if (limit <= 0) return null;

        // 1. Words visible on the same line.
        if (contextWords != null)
        {
            var fromContext = BestCandidate(clean, contextWords.Select(EnglishWordList.Normalize), limit);
            if (fromContext != null) return fromContext;
        }

        // 2. The frequency list, narrowed by length so this stays a scan of a few thousand
        //    candidates rather than all 47,000.
        var candidates = EnglishWordList.ByFrequency
            .Where(w => Math.Abs(w.Length - clean.Length) <= limit);

        return BestCandidate(clean, candidates, limit);
    }

    /// <summary>
    /// Picks the single best candidate, or nothing.
    ///
    /// Ties are the interesting case. Rejecting every tie outright leaves obvious repairs
    /// unmade; accepting them invents words. The rule here is that a tie is broken only when one
    /// candidate is *decisively* more common than the other - a word people actually read beats a
    /// rare one at equal distance - and is otherwise abandoned.
    /// </summary>
    private static string? BestCandidate(string token, IEnumerable<string> candidates, double limit)
    {
        string? best = null;
        var bestDistance = double.MaxValue;
        var bestRank = int.MaxValue;
        string? runnerUp = null;
        var runnerUpRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0 || candidate == token) continue;

            var distance = Distance(token, candidate);
            if (distance > limit) continue;

            var rank = EnglishWordList.Rank(candidate);
            if (rank < 0) rank = int.MaxValue - 1;

            if (distance < bestDistance - 0.0001)
            {
                runnerUp = best;
                runnerUpRank = bestRank;
                best = candidate;
                bestDistance = distance;
                bestRank = rank;
            }
            else if (Math.Abs(distance - bestDistance) <= 0.0001 && candidate != best)
            {
                // Equally close. Keep whichever is more common, remember the other.
                if (rank < bestRank)
                {
                    runnerUp = best;
                    runnerUpRank = bestRank;
                    best = candidate;
                    bestRank = rank;
                }
                else if (rank < runnerUpRank)
                {
                    runnerUp = candidate;
                    runnerUpRank = rank;
                }
            }
        }

        if (best == null) return null;

        // A near-tie is only resolved when the winner is several times more common. Two words
        // that are equally close and equally ordinary are genuinely ambiguous, and the raw token
        // is a better thing to show than a guess.
        if (runnerUp != null && runnerUpRank < int.MaxValue - 1)
        {
            var decisive = bestRank * 3 < runnerUpRank;
            if (!decisive) return null;
        }

        return best;
    }

    /// <summary>
    /// Every position at which <paramref name="token"/> can be cut into two independently real
    /// words. Both halves must stand on their own - one coincidental match is not evidence, since
    /// almost any string can be cut somewhere that leaves a valid fragment.
    ///
    /// Shared by both split entry points below. They previously had separate copies of this loop
    /// in two different projects, and only one of them was covered by tests - so the tested
    /// behaviour was not the shipped behaviour.
    /// </summary>
    private static IEnumerable<(string Left, string Right, int Index)> ValidSplits(string token)
    {
        for (var i = 2; i <= token.Length - 2; i++)
        {
            var left = token[..i];
            var right = token[i..];

            if (EnglishWordList.IsRealWord(left) && EnglishWordList.IsRealWord(right))
                yield return (left, right, i);
        }
    }

    /// <summary>
    /// Splits a token OCR merged out of two words ("myheart"), returning the halves, or null when
    /// it isn't a merge. The split is required to be unambiguous: "together" could be cut into
    /// "to"+"gether" or "toge"+"ther" and only one of those is a real pair, but where several
    /// work, we don't guess.
    /// </summary>
    public static (string Left, string Right)? SplitMerged(string token)
    {
        var clean = EnglishWordList.Normalize(token ?? "");
        if (clean.Length < 4 || EnglishWordList.IsRealWord(clean)) return null;

        (string Left, string Right)? found = null;
        var foundScore = int.MaxValue;
        var ambiguous = false;

        foreach (var (left, right, _) in ValidSplits(clean))
        {
            // Prefer the split whose halves are commonest overall - "my"+"heart" over a pairing
            // of two obscure words that happen to also be real.
            var score = EnglishWordList.Rank(left) + EnglishWordList.Rank(right);
            if (score < foundScore)
            {
                if (found != null) ambiguous = true;
                found = (left, right);
                foundScore = score;
            }
            else
            {
                ambiguous = true;
            }
        }

        // Several plausible splits and no clear winner: leave it alone.
        if (ambiguous && foundScore > 20_000) return null;

        return found;
    }

    /// <summary>
    /// The cursor-aware split, for when we know <em>where in the token</em> the user was pointing.
    /// Returns whichever half sits under <paramref name="relativePosition"/> (0 = left edge of the
    /// token's bounding box, 1 = right edge), assuming roughly uniform glyph width - close enough
    /// to tell which of two merged words was actually being pointed at.
    ///
    /// Unlike <see cref="SplitMerged"/> this tolerates ambiguity, because the cursor position is
    /// itself the tie-breaker: the split closest to where the user pointed wins.
    /// </summary>
    public static string? SplitMergedAt(string token, double relativePosition)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var position = Math.Clamp(relativePosition, 0.0, 1.0);

        // The normalized form first, then the raw token - the latter keeps apostrophes, which is
        // what lets a genuine contraction survive being merged with its neighbour.
        var clean = EnglishWordList.Normalize(token);
        return HalfUnderCursor(clean, position) ?? HalfUnderCursor(token, position);

        static string? HalfUnderCursor(string candidate, double position)
        {
            if (candidate.Length < 4) return null;

            string? best = null;
            var bestDistance = double.MaxValue;

            foreach (var (left, right, index) in ValidSplits(candidate))
            {
                var splitFraction = (double)index / candidate.Length;
                var distance = Math.Abs(splitFraction - position);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = position <= splitFraction ? left : right;
            }

            return best;
        }
    }
}
