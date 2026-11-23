namespace TaskHubGreeter.Client.Extras;

public sealed record Quote(string Text, string Author);

/// <summary>A short, offline quote per day (rotates by date; the same all day).</summary>
public static class QuoteOfTheDay
{
    // Classic, public-domain sayings.
    public static IReadOnlyList<Quote> All { get; } =
    [
        new("Well begun is half done.", "Aristotle"),
        new("The journey of a thousand miles begins with a single step.", "Lao Tzu"),
        new("Lost time is never found again.", "Benjamin Franklin"),
        new("Energy and persistence conquer all things.", "Benjamin Franklin"),
        new("One today is worth two tomorrows.", "Benjamin Franklin"),
        new("Diligence is the mother of good luck.", "Benjamin Franklin"),
        new("Do what you can, with what you have, where you are.", "Theodore Roosevelt"),
        new("Knowing is not enough; we must apply.", "Johann Wolfgang von Goethe"),
        new("He who has a why to live can bear almost any how.", "Friedrich Nietzsche"),
        new("Arise, awake, and stop not till the goal is reached.", "Swami Vivekananda"),
        new("Great things are done by a series of small things brought together.", "Vincent van Gogh"),
        new("The best way out is always through.", "Robert Frost"),
        new("Whatever is worth doing at all is worth doing well.", "Lord Chesterfield"),
        new("To be prepared is half the victory.", "Miguel de Cervantes"),
        new("Begin at the beginning, and go on till you come to the end: then stop.", "Lewis Carroll"),
        new("The happiness of your life depends upon the quality of your thoughts.", "Marcus Aurelius"),
        new("Take rest; a field that has rested gives a bountiful crop.", "Ovid"),
        new("Fortune favours the bold.", "Virgil"),
        new("It does not matter how slowly you go as long as you do not stop.", "Confucius"),
        new("Patience is bitter, but its fruit is sweet.", "Jean-Jacques Rousseau"),
        new("Nothing will come of nothing.", "William Shakespeare"),
        new("We are what we repeatedly do.", "Will Durant, after Aristotle"),
        new("Rome was not built in a day.", "Proverb"),
        new("Order and simplification are the first steps toward the mastery of a subject.", "Thomas Mann"),
        new("Never put off till tomorrow what you can do today.", "Proverb"),
        new("Little strokes fell great oaks.", "Benjamin Franklin"),
        new("Make hay while the sun shines.", "Proverb"),
        new("Life is really simple, but we insist on making it complicated.", "Confucius"),
    ];

    public static Quote For(DateOnly date) => All[date.DayNumber % All.Count];
}
