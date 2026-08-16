namespace Tarjem.Core.Translation;

/// <summary>
/// The prompts every AI provider shares, in one place so Gemini, Groq and Cerebras cannot drift
/// into translating the same sentence to different standards.
///
/// The governing instruction is transcreation, not translation: produce the sentence a native
/// writer would have written to say this, not a word-shaped mapping of the original. Literal
/// output is the default failure mode of every one of these models - it reads as machine
/// translation precisely because it preserves the source language's word order and idiom - so
/// the instruction to abandon the original's structure has to be explicit and repeated.
/// </summary>
public static class TranslationPrompts
{
    /// <summary>The standing role every AI translation call opens with.</summary>
    public static string SystemInstruction(string targetLanguageName) =>
        $@"You are a native {targetLanguageName} writer producing text for {targetLanguageName} readers.

You do not translate word by word. You read the whole meaning, then write it again from scratch in {targetLanguageName}, the way someone who thinks in {targetLanguageName} would have said it. Rearrange word order, change grammatical structure, and replace idioms with the natural {targetLanguageName} equivalent whenever that reads better - matching the original's structure is never a goal.

Match the register and tone of the original: casual stays casual, formal stays formal, and game or story dialogue keeps its character and energy.

Use only native {targetLanguageName} script and spelling. Never transliterate, never leave source-language words in, never add notes, alternatives, parentheses or explanations.";

    /// <summary>Region (Alt+W) translation: an arbitrary block of text lifted off the screen.</summary>
    public static string TextTranslationRequest(string text, string sourceLanguageName, string targetLanguageName) =>
        $@"Rewrite this {sourceLanguageName} text in natural {targetLanguageName}.

Keep the original line breaks, because the result is drawn back over the layout it came from. The text was read off a screen by OCR, so it may contain small recognition errors - infer what was meant and translate that, rather than reproducing an obvious typo.

Return only the {targetLanguageName} text. No quotes, no commentary, nothing before or after it.

Text:
{text}";

    /// <summary>Word (Alt+Q) translation for providers that answer in plain text rather than
    /// structured JSON. Two bare lines are far more reliable from smaller models than a schema.</summary>
    public static string WordTranslationRequest(string word, string sentence, string sourceLanguageName, string targetLanguageName)
    {
        var hasSentence = !string.IsNullOrWhiteSpace(sentence);

        return $@"{sourceLanguageName} word: ""{word}""
{(hasSentence
    ? $@"It appears in this sentence: ""{sentence}""
Translate the word in the sense it carries in that sentence - not its most common dictionary sense, if the two differ."
    : "No sentence was given, so use the word's most common sense.")}

Answer in exactly {(hasSentence ? "two lines" : "one line")}, with no labels and nothing else:
Line 1: the word in {targetLanguageName}
{(hasSentence ? $"Line 2: the whole sentence rewritten in natural {targetLanguageName}" : "")}";
    }

    /// <summary>Word (Alt+Q) lookup for providers that support a response schema, which gets both
    /// the dictionary entry and the translation from a single request.</summary>
    public static string DictionaryEntryRequest(string word, string fullSentence, string sourceLanguageName, string targetLanguageName)
    {
        var hasSentence = !string.IsNullOrEmpty(fullSentence);

        return $@"You are a precise {sourceLanguageName} dictionary and a native {targetLanguageName} writer at the same time. Never leave a field blank, null, or a placeholder - if you are unsure, give your single best answer instead of omitting it.

Word to analyze (in {sourceLanguageName}): ""{word}""
{(hasSentence
    ? $@"Sentence it appears in: ""{fullSentence}""
Use this sentence to pick the word's exact sense, part of speech, and grammatical form here - do not default to the word's most common dictionary sense if the context calls for a different one."
    : "No sentence was given - use the word's single most common sense.")}

Return a single JSON object with exactly these fields:
- ""word"": the word/phrase exactly as it should be written in {sourceLanguageName}, respecting that language's normal capitalization rules (not necessarily lowercase - e.g. German nouns are capitalized)
- ""phonetic"": IPA phonetic transcription, e.g. ""/ˈmiːnɪŋ/""
- ""partOfSpeech"": one of noun, verb, adjective, adverb, pronoun, preposition, conjunction, interjection, determiner - matching how it is used above
- ""cefrLevel"": one of A1, A2, B1, B2, C1, C2 - the word's difficulty level for a learner of {sourceLanguageName}
- ""definition"": a clear, self-contained definition in 1-2 sentences, written in {sourceLanguageName}, in the sense that matches the context above - do not define an unrelated sense of the word
- ""synonyms"": array of 3-5 {sourceLanguageName} synonyms, same part of speech and sense as ""definition""
- ""arabicWord"": the natural {targetLanguageName} word a native speaker would actually use for this, in the same sense as ""definition"". Native {targetLanguageName} script only - no transliteration, no {sourceLanguageName}, no parenthetical notes. (Despite the field's name, use {targetLanguageName}, not Arabic specifically - the name is just this field's fixed key.)
- ""arabicTranslation"": identical to ""arabicWord""
{(hasSentence
    ? $@"- ""fullSentenceTranslation"": the sentence above rewritten in natural, fluent {targetLanguageName} - as a native writer would say it, not as a word-by-word mapping. Rearrange the structure freely and swap idioms for their {targetLanguageName} equivalents whenever it reads better. Within this string, wrap ONLY the exact word or phrase that carries the meaning of ""{word}"" in double asterisks, e.g. **word**. If {targetLanguageName} grammar attaches a prefix or suffix to that word (an article, preposition, case ending, etc.), include it inside the asterisks too. Wrap exactly one occurrence and nothing else in the sentence."
    : "")}

Return ONLY the raw JSON object. No markdown code fences, no commentary, nothing before or after the JSON.";
    }
}
