using Unit3dDescriptionClone.Config;
using Unit3dDescriptionClone.Http;
using Unit3dDescriptionClone.Services;

Directory.CreateDirectory("cache");

var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var positional = new List<string>();
string? fromTorrentId = null;
string? fromTrackerName = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--from-id", StringComparison.OrdinalIgnoreCase))
    {
        if (++i >= args.Length || args[i].StartsWith('-'))
        {
            Console.Error.WriteLine("--from-id requires a value.");
            return 1;
        }
        fromTorrentId = args[i];
    }
    else if (args[i].StartsWith("--from-id=", StringComparison.OrdinalIgnoreCase))
    {
        fromTorrentId = args[i][(args[i].IndexOf('=') + 1)..];
        if (string.IsNullOrWhiteSpace(fromTorrentId))
        {
            Console.Error.WriteLine("--from-id requires a value.");
            return 1;
        }
    }
    else if (args[i].StartsWith('-'))
        flags.Add(args[i]);
    else
        positional.Add(args[i]);
}

if (fromTorrentId is not null)
{
    var separatorIndex = fromTorrentId.IndexOf('/');
    if (separatorIndex <= 0 || separatorIndex == fromTorrentId.Length - 1 || fromTorrentId.IndexOf('/', separatorIndex + 1) >= 0)
    {
        Console.Error.WriteLine("--from-id requires a value in the format <from-tracker>/<id>, for example aither/12345.");
        return 1;
    }
    fromTrackerName = fromTorrentId[..separatorIndex];
    fromTorrentId = fromTorrentId[(separatorIndex + 1)..];
}

var skipRehosting = flags.Contains("--no-rehost");
var skipAppend = flags.Contains("--no-append");
var allowRerun = flags.Contains("--allow-rerun");

if (positional.Count == 0 || (positional[0] == "backfill" && positional.Count < 3))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] [--no-append] [--allow-rerun] [--from-id <from-tracker>/<id>] <torrent-id>");
    Console.Error.WriteLine("  unit3d-description-clone [--no-rehost] [--no-append] [--allow-rerun] backfill <release-group> <uploader>");
    Console.Error.WriteLine("  unit3d-description-clone groups");
    return 1;
}
if (positional[0] == "backfill" && fromTorrentId is not null)
{
    Console.Error.WriteLine("--from-id cannot be used with backfill.");
    return 1;
}

var config = AppConfig.Load("unit3d-description-clone.ini");

if (positional[0] == "groups")
{
    Console.WriteLine(string.Join(", ", config.FromTrackers.SelectMany(ft => ft.ReleaseGroups)));
    return 0;
}

var cookies = CookieStore.Load("cache/target-cookies.json", config.ToTrackerUrl);
using var noRedirectClient = HttpClientFactory.Create(cookies, followRedirects: false);
using var autoRedirectClient = HttpClientFactory.Create(cookies, followRedirects: true);
// Dedicated client for image/click-through rehosting, deliberately NOT sharing
// autoRedirectClient's flat 30s HttpClient.Timeout (which caps the entire request
// including the body download, not just connecting). That flat cap made a slow-but-
// genuinely-working download of a large image (confirmed real case: beyondhd.co
// screenshots, which sometimes take well over 30s despite the file arriving fine)
// almost certain to fail every one of its retries too - the same wasted-effort loop
// as "guaranteed to fail, not actually protecting against a stuck connection". Left
// at Timeout.InfiniteTimeSpan here since ImageRehoster enforces its own stall-based
// timeout instead (see DownloadBytesWithProgressAsync) - one that resets on every
// chunk actually received, so a connection that's genuinely making progress is never
// killed just for being slow, while one that goes truly silent still gets caught
// quickly. Every OTHER use of autoRedirectClient (the tracker's own API/web calls)
// keeps its original 30s protection unchanged.
using var imageClient = HttpClientFactory.Create(cookies, followRedirects: true, timeout: Timeout.InfiniteTimeSpan);

var unit3dApi = new Unit3dApiClient(autoRedirectClient, config);
var f3nixApi = new F3nixApiClient(autoRedirectClient);
var torznabApi = new TorznabApiClient(autoRedirectClient);
var web = new Unit3dWebClient(noRedirectClient, autoRedirectClient, cookies, config);
var imageRehoster = new ImageRehoster(imageClient, config);
var cloner = new DescriptionCloner(unit3dApi, f3nixApi, torznabApi, web, imageRehoster, config);

try
{
    if (positional[0] == "backfill")
    {
        await cloner.BackfillAsync(positional[1], positional[2], skipRehosting, skipAppend, allowRerun);
        return 0;
    }

    var outcome = await cloner.CloneAsync(positional[0], skipRehosting, skipAppend, allowRerun, fromTrackerName, fromTorrentId);
    return outcome == CloneOutcome.Failed ? 1 : 0;
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    return 1;
}
