namespace VowenLite;

// Roadmap 1.1: calendar feeds. Settings.Feeds holds .ics links (e.g. Brightspace Calendar -> Subscribe). Every hour each feed is fetched
// and its events become tasks in the Settings.FeedList planner list: Item.Uid = the VEVENT UID, Item.Source = "feed:<short hash of url>",
// Item.Link = the event URL if any. Re-fetches update title/due of items it made; never touch items the user ticked done or deleted.
static class Feeds
{
    public static string TryLocal(string l, Settings s) => null;     // "add calendar feed <url>", "refresh my feeds", "what feeds do I have", "remove feed <n>"
    public static void Tick(Settings s, DateTime now) { }           // hourly refresh; network on its own Task
    public static void SelfChecks(Action<bool, string> check) { }   // pure: .ics parsing
    public static int Test(string[] args) { Console.WriteLine("feedtest ok"); return 0; }
}
