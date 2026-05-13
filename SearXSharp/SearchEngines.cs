using SearXSharp.Engines;

namespace SearXSharp;

/// <summary>
/// Static factory class providing categorized access to all search engines.
/// Use this to register engines with SearchEngineManager easily.
/// </summary>
public static class SearchEngines
{

    // ─── Files (5 engines) ───
    public static ISearchEngine AnnasArchive(ILogger? logger = null)
        => logger is null ? new AnnasArchiveSearchEngine() : new AnnasArchiveSearchEngine(logger);
    public static ISearchEngine Kickass(ILogger? logger = null)
        => logger is null ? new KickassSearchEngine() : new KickassSearchEngine(logger);
    public static ISearchEngine Nyaa(ILogger? logger = null)
        => logger is null ? new NyaaSearchEngine() : new NyaaSearchEngine(logger);
    public static ISearchEngine PirateBay(ILogger? logger = null)
        => logger is null ? new PirateBaySearchEngine() : new PirateBaySearchEngine(logger);
    public static ISearchEngine ZLibrary(ILogger? logger = null)
        => logger is null ? new ZLibrarySearchEngine() : new ZLibrarySearchEngine(logger);
    public static ISearchEngine _1337x(ILogger? logger = null)
        => logger is null ? new _1337xSearchEngine() : new _1337xSearchEngine(logger);

    // ─── Apps (1 engines) ───
    public static ISearchEngine AppleAppStore(ILogger? logger = null)
        => logger is null ? new AppleAppStoreSearchEngine() : new AppleAppStoreSearchEngine(logger);

    // ─── IT (12 engines) ───
    public static ISearchEngine ArchLinux(ILogger? logger = null)
        => logger is null ? new ArchLinuxSearchEngine() : new ArchLinuxSearchEngine(logger);
    public static ISearchEngine GitHub(ILogger? logger = null)
        => logger is null ? new GitHubSearchEngine() : new GitHubSearchEngine(logger);
    public static ISearchEngine GitLab(ILogger? logger = null)
        => logger is null ? new GitLabSearchEngine() : new GitLabSearchEngine(logger);
    public static ISearchEngine HackerNews(ILogger? logger = null)
        => logger is null ? new HackerNewsSearchEngine() : new HackerNewsSearchEngine(logger);
    public static ISearchEngine HuggingFace(ILogger? logger = null)
        => logger is null ? new HuggingFaceSearchEngine() : new HuggingFaceSearchEngine(logger);
    public static ISearchEngine MicrosoftLearn(ILogger? logger = null)
        => logger is null ? new MicrosoftLearnSearchEngine() : new MicrosoftLearnSearchEngine(logger);
    public static ISearchEngine NVD(ILogger? logger = null)
        => logger is null ? new NVDSearchEngine() : new NVDSearchEngine(logger);
    public static ISearchEngine Ollama(ILogger? logger = null)
        => logger is null ? new OllamaSearchEngine() : new OllamaSearchEngine(logger);
    public static ISearchEngine SourceHut(ILogger? logger = null)
        => logger is null ? new SourceHutSearchEngine() : new SourceHutSearchEngine(logger);
    public static ISearchEngine StackExchange(ILogger? logger = null)
        => logger is null ? new StackExchangeSearchEngine() : new StackExchangeSearchEngine(logger);
    public static ISearchEngine Steam(ILogger? logger = null)
        => logger is null ? new SteamSearchEngine() : new SteamSearchEngine(logger);
    public static ISearchEngine Elasticsearch(ILogger? logger = null)
        => logger is null ? new ElasticsearchSearchEngine() : new ElasticsearchSearchEngine(logger);

    // ─── Science (7 engines) ───
    public static ISearchEngine Arxiv(ILogger? logger = null)
        => logger is null ? new ArxivSearchEngine() : new ArxivSearchEngine(logger);
    public static ISearchEngine GoogleScholar(ILogger? logger = null)
        => logger is null ? new GoogleScholarSearchEngine() : new GoogleScholarSearchEngine(logger);
    public static ISearchEngine OpenAlex(ILogger? logger = null)
        => logger is null ? new OpenAlexSearchEngine() : new OpenAlexSearchEngine(logger);
    public static ISearchEngine Pubmed(ILogger? logger = null)
        => logger is null ? new PubmedSearchEngine() : new PubmedSearchEngine(logger);
    public static ISearchEngine SemanticScholar(ILogger? logger = null)
        => logger is null ? new SemanticScholarSearchEngine() : new SemanticScholarSearchEngine(logger);
    public static ISearchEngine WolframAlpha(ILogger? logger = null)
        => logger is null ? new WolframAlphaSearchEngine() : new WolframAlphaSearchEngine(logger);
    public static ISearchEngine Wikipedia(ILogger? logger = null)
        => logger is null ? new WikipediaSearchEngine() : new WikipediaSearchEngine(logger);

    // ─── General (22 engines) ───
    public static ISearchEngine Ask(ILogger? logger = null)
        => logger is null ? new AskSearchEngine() : new AskSearchEngine(logger);
    public static ISearchEngine Baidu(ILogger? logger = null)
        => logger is null ? new BaiduSearchEngine() : new BaiduSearchEngine(logger);
    public static ISearchEngine Bing(ILogger? logger = null)
        => logger is null ? new BingSearchEngine() : new BingSearchEngine(logger);
    public static ISearchEngine Bpb(ILogger? logger = null)
        => logger is null ? new BpbSearchEngine() : new BpbSearchEngine(logger);
    public static ISearchEngine Brave(ILogger? logger = null)
        => logger is null ? new BraveSearchEngine() : new BraveSearchEngine(logger);
    public static ISearchEngine DuckDuckGo(ILogger? logger = null)
        => logger is null ? new DuckDuckGoSearchEngine() : new DuckDuckGoSearchEngine(logger);
    public static ISearchEngine Duden(ILogger? logger = null)
        => logger is null ? new DudenSearchEngine() : new DudenSearchEngine(logger);
    public static ISearchEngine Emojipedia(ILogger? logger = null)
        => logger is null ? new EmojipediaSearchEngine() : new EmojipediaSearchEngine(logger);
    public static ISearchEngine Geizhals(ILogger? logger = null)
        => logger is null ? new GeizhalsSearchEngine() : new GeizhalsSearchEngine(logger);
    public static ISearchEngine Google(ILogger? logger = null)
        => logger is null ? new GoogleSearchEngine() : new GoogleSearchEngine(logger);
    public static ISearchEngine Mediawiki(ILogger? logger = null)
        => logger is null ? new MediawikiSearchEngine() : new MediawikiSearchEngine(logger);
    public static ISearchEngine Mojeek(ILogger? logger = null)
        => logger is null ? new MojeekSearchEngine() : new MojeekSearchEngine(logger);
    public static ISearchEngine Mwmbl(ILogger? logger = null)
        => logger is null ? new MwmblSearchEngine() : new MwmblSearchEngine(logger);
    public static ISearchEngine OpenMeteo(ILogger? logger = null)
        => logger is null ? new OpenMeteoSearchEngine() : new OpenMeteoSearchEngine(logger);
    public static ISearchEngine Qwant(ILogger? logger = null)
        => logger is null ? new QwantSearchEngine() : new QwantSearchEngine(logger);
    public static ISearchEngine Searx(ILogger? logger = null)
        => logger is null ? new SearxSearchEngine() : new SearxSearchEngine(logger);
    public static ISearchEngine Seznam(ILogger? logger = null)
        => logger is null ? new SeznamSearchEngine() : new SeznamSearchEngine(logger);
    public static ISearchEngine Sogou(ILogger? logger = null)
        => logger is null ? new SogouSearchEngine() : new SogouSearchEngine(logger);
    public static ISearchEngine Startpage(ILogger? logger = null)
        => logger is null ? new StartpageSearchEngine() : new StartpageSearchEngine(logger);
    public static ISearchEngine Wordnik(ILogger? logger = null)
        => logger is null ? new WordnikSearchEngine() : new WordnikSearchEngine(logger);
    public static ISearchEngine Wttr(ILogger? logger = null)
        => logger is null ? new WttrSearchEngine() : new WttrSearchEngine(logger);
    public static ISearchEngine Yahoo(ILogger? logger = null)
        => logger is null ? new YahooSearchEngine() : new YahooSearchEngine(logger);
    public static ISearchEngine Yandex(ILogger? logger = null)
        => logger is null ? new YandexSearchEngine() : new YandexSearchEngine(logger);

    // ─── Music (9 engines) ───
    public static ISearchEngine Bandcamp(ILogger? logger = null)
        => logger is null ? new BandcampSearchEngine() : new BandcampSearchEngine(logger);
    public static ISearchEngine Deezer(ILogger? logger = null)
        => logger is null ? new DeezerSearchEngine() : new DeezerSearchEngine(logger);
    public static ISearchEngine Fyyd(ILogger? logger = null)
        => logger is null ? new FyydSearchEngine() : new FyydSearchEngine(logger);
    public static ISearchEngine Genius(ILogger? logger = null)
        => logger is null ? new GeniusSearchEngine() : new GeniusSearchEngine(logger);
    public static ISearchEngine Mixcloud(ILogger? logger = null)
        => logger is null ? new MixcloudSearchEngine() : new MixcloudSearchEngine(logger);
    public static ISearchEngine PodcastIndex(ILogger? logger = null)
        => logger is null ? new PodcastIndexSearchEngine() : new PodcastIndexSearchEngine(logger);
    public static ISearchEngine SoundCloud(ILogger? logger = null)
        => logger is null ? new SoundCloudSearchEngine() : new SoundCloudSearchEngine(logger);
    public static ISearchEngine Spotify(ILogger? logger = null)
        => logger is null ? new SpotifySearchEngine() : new SpotifySearchEngine(logger);
    public static ISearchEngine YandexMusic(ILogger? logger = null)
        => logger is null ? new YandexMusicSearchEngine() : new YandexMusicSearchEngine(logger);

    // ─── Videos (14 engines) ───
    public static ISearchEngine Bilibili(ILogger? logger = null)
        => logger is null ? new BilibiliSearchEngine() : new BilibiliSearchEngine(logger);
    public static ISearchEngine BingVideos(ILogger? logger = null)
        => logger is null ? new BingVideosSearchEngine() : new BingVideosSearchEngine(logger);
    public static ISearchEngine Bitchute(ILogger? logger = null)
        => logger is null ? new BitchuteSearchEngine() : new BitchuteSearchEngine(logger);
    public static ISearchEngine Dailymotion(ILogger? logger = null)
        => logger is null ? new DailymotionSearchEngine() : new DailymotionSearchEngine(logger);
    public static ISearchEngine GoogleVideos(ILogger? logger = null)
        => logger is null ? new GoogleVideosSearchEngine() : new GoogleVideosSearchEngine(logger);
    public static ISearchEngine Ina(ILogger? logger = null)
        => logger is null ? new InaSearchEngine() : new InaSearchEngine(logger);
    public static ISearchEngine Invidious(ILogger? logger = null)
        => logger is null ? new InvidiousSearchEngine() : new InvidiousSearchEngine(logger);
    public static ISearchEngine Niconico(ILogger? logger = null)
        => logger is null ? new NiconicoSearchEngine() : new NiconicoSearchEngine(logger);
    public static ISearchEngine Odysee(ILogger? logger = null)
        => logger is null ? new OdyseeSearchEngine() : new OdyseeSearchEngine(logger);
    public static ISearchEngine PeerTube(ILogger? logger = null)
        => logger is null ? new PeerTubeSearchEngine() : new PeerTubeSearchEngine(logger);
    public static ISearchEngine Piped(ILogger? logger = null)
        => logger is null ? new PipedSearchEngine() : new PipedSearchEngine(logger);
    public static ISearchEngine Rumble(ILogger? logger = null)
        => logger is null ? new RumbleSearchEngine() : new RumbleSearchEngine(logger);
    public static ISearchEngine Vimeo(ILogger? logger = null)
        => logger is null ? new VimeoSearchEngine() : new VimeoSearchEngine(logger);
    public static ISearchEngine YouTube(ILogger? logger = null)
        => logger is null ? new YouTubeSearchEngine() : new YouTubeSearchEngine(logger);

    // ─── Images (19 engines) ───
    public static ISearchEngine ArtStation(ILogger? logger = null)
        => logger is null ? new ArtStationSearchEngine() : new ArtStationSearchEngine(logger);
    public static ISearchEngine BingImages(ILogger? logger = null)
        => logger is null ? new BingImagesSearchEngine() : new BingImagesSearchEngine(logger);
    public static ISearchEngine DeviantArt(ILogger? logger = null)
        => logger is null ? new DeviantArtSearchEngine() : new DeviantArtSearchEngine(logger);
    public static ISearchEngine FindThatMeme(ILogger? logger = null)
        => logger is null ? new FindThatMemeSearchEngine() : new FindThatMemeSearchEngine(logger);
    public static ISearchEngine Flickr(ILogger? logger = null)
        => logger is null ? new FlickrSearchEngine() : new FlickrSearchEngine(logger);
    public static ISearchEngine Frinkiac(ILogger? logger = null)
        => logger is null ? new FrinkiacSearchEngine() : new FrinkiacSearchEngine(logger);
    public static ISearchEngine GoogleImages(ILogger? logger = null)
        => logger is null ? new GoogleImagesSearchEngine() : new GoogleImagesSearchEngine(logger);
    public static ISearchEngine Imgur(ILogger? logger = null)
        => logger is null ? new ImgurSearchEngine() : new ImgurSearchEngine(logger);
    public static ISearchEngine Ipernity(ILogger? logger = null)
        => logger is null ? new IpernitySearchEngine() : new IpernitySearchEngine(logger);
    public static ISearchEngine Lucide(ILogger? logger = null)
        => logger is null ? new LucideSearchEngine() : new LucideSearchEngine(logger);
    public static ISearchEngine MaterialIcons(ILogger? logger = null)
        => logger is null ? new MaterialIconsSearchEngine() : new MaterialIconsSearchEngine(logger);
    public static ISearchEngine OpenClipart(ILogger? logger = null)
        => logger is null ? new OpenClipartSearchEngine() : new OpenClipartSearchEngine(logger);
    public static ISearchEngine Openverse(ILogger? logger = null)
        => logger is null ? new OpenverseSearchEngine() : new OpenverseSearchEngine(logger);
    public static ISearchEngine Pexels(ILogger? logger = null)
        => logger is null ? new PexelsSearchEngine() : new PexelsSearchEngine(logger);
    public static ISearchEngine Pinterest(ILogger? logger = null)
        => logger is null ? new PinterestSearchEngine() : new PinterestSearchEngine(logger);
    public static ISearchEngine Pixabay(ILogger? logger = null)
        => logger is null ? new PixabaySearchEngine() : new PixabaySearchEngine(logger);
    public static ISearchEngine Pixiv(ILogger? logger = null)
        => logger is null ? new PixivSearchEngine() : new PixivSearchEngine(logger);
    public static ISearchEngine Unsplash(ILogger? logger = null)
        => logger is null ? new UnsplashSearchEngine() : new UnsplashSearchEngine(logger);
    public static ISearchEngine Wallhaven(ILogger? logger = null)
        => logger is null ? new WallhavenSearchEngine() : new WallhavenSearchEngine(logger);
    public static ISearchEngine WikiCommons(ILogger? logger = null)
        => logger is null ? new WikiCommonsSearchEngine() : new WikiCommonsSearchEngine(logger);

    // ─── News (5 engines) ───
    public static ISearchEngine BingNews(ILogger? logger = null)
        => logger is null ? new BingNewsSearchEngine() : new BingNewsSearchEngine(logger);
    public static ISearchEngine GoogleNews(ILogger? logger = null)
        => logger is null ? new GoogleNewsSearchEngine() : new GoogleNewsSearchEngine(logger);
    public static ISearchEngine IlPost(ILogger? logger = null)
        => logger is null ? new IlPostSearchEngine() : new IlPostSearchEngine(logger);
    public static ISearchEngine Reuters(ILogger? logger = null)
        => logger is null ? new ReutersSearchEngine() : new ReutersSearchEngine(logger);
    public static ISearchEngine YahooNews(ILogger? logger = null)
        => logger is null ? new YahooNewsSearchEngine() : new YahooNewsSearchEngine(logger);

    // ─── Packages (11 engines) ───
    public static ISearchEngine AlpineLinux(ILogger? logger = null)
        => logger is null ? new AlpineLinuxSearchEngine() : new AlpineLinuxSearchEngine(logger);
    public static ISearchEngine Crates(ILogger? logger = null)
        => logger is null ? new CratesSearchEngine() : new CratesSearchEngine(logger);
    public static ISearchEngine DockerHub(ILogger? logger = null)
        => logger is null ? new DockerHubSearchEngine() : new DockerHubSearchEngine(logger);
    public static ISearchEngine FDroid(ILogger? logger = null)
        => logger is null ? new FDroidSearchEngine() : new FDroidSearchEngine(logger);
    public static ISearchEngine Hex(ILogger? logger = null)
        => logger is null ? new HexSearchEngine() : new HexSearchEngine(logger);
    public static ISearchEngine MetaCPAN(ILogger? logger = null)
        => logger is null ? new MetaCPANSearchEngine() : new MetaCPANSearchEngine(logger);
    public static ISearchEngine Npm(ILogger? logger = null)
        => logger is null ? new NpmSearchEngine() : new NpmSearchEngine(logger);
    public static ISearchEngine NuGet(ILogger? logger = null)
        => logger is null ? new NuGetSearchEngine() : new NuGetSearchEngine(logger);
    public static ISearchEngine PkgGoDev(ILogger? logger = null)
        => logger is null ? new PkgGoDevSearchEngine() : new PkgGoDevSearchEngine(logger);
    public static ISearchEngine Pypi(ILogger? logger = null)
        => logger is null ? new PypiSearchEngine() : new PypiSearchEngine(logger);

    // ─── SocialMedia (5 engines) ───
    public static ISearchEngine Discourse(ILogger? logger = null)
        => logger is null ? new DiscourseSearchEngine() : new DiscourseSearchEngine(logger);
    public static ISearchEngine Lemmy(ILogger? logger = null)
        => logger is null ? new LemmySearchEngine() : new LemmySearchEngine(logger);
    public static ISearchEngine Mastodon(ILogger? logger = null)
        => logger is null ? new MastodonSearchEngine() : new MastodonSearchEngine(logger);
    public static ISearchEngine NineGag(ILogger? logger = null)
        => logger is null ? new NineGagSearchEngine() : new NineGagSearchEngine(logger);
    public static ISearchEngine Reddit(ILogger? logger = null)
        => logger is null ? new RedditSearchEngine() : new RedditSearchEngine(logger);

    // ─── Shopping (2 engines) ───
    public static ISearchEngine Ebay(ILogger? logger = null)
        => logger is null ? new EbaySearchEngine() : new EbaySearchEngine(logger);
    public static ISearchEngine Imdb(ILogger? logger = null)
        => logger is null ? new ImdbSearchEngine() : new ImdbSearchEngine(logger);

    // ─── Books (2 engines) ───
    public static ISearchEngine Goodreads(ILogger? logger = null)
        => logger is null ? new GoodreadsSearchEngine() : new GoodreadsSearchEngine(logger);
    public static ISearchEngine OpenLibrary(ILogger? logger = null)
        => logger is null ? new OpenLibrarySearchEngine() : new OpenLibrarySearchEngine(logger);

    // ─── Map (2 engines) ───
    public static ISearchEngine OpenStreetMap(ILogger? logger = null)
        => logger is null ? new OpenStreetMapSearchEngine() : new OpenStreetMapSearchEngine(logger);
    public static ISearchEngine Photon(ILogger? logger = null)
        => logger is null ? new PhotonSearchEngine() : new PhotonSearchEngine(logger);

	// ─── Category batch factories ───

	public static IEnumerable<ISearchEngine> AllEngines(ILogger? logger = null)
    {
        foreach (var engine in FilesEngines(logger)
            .Concat(AppsEngines(logger))
            .Concat(ITEngines(logger))
            .Concat(ScienceEngines(logger))
            .Concat(GeneralEngines(logger))
            .Concat(MusicEngines(logger))
            .Concat(VideosEngines(logger))
            .Concat(ImagesEngines(logger))
            .Concat(NewsEngines(logger))
            .Concat(PackagesEngines(logger))
            .Concat(SocialMediaEngines(logger))
            .Concat(ShoppingEngines(logger))
            .Concat(BooksEngines(logger))
            .Concat(MapEngines(logger)))
        {
            yield return engine;
        }
    }

    public static IEnumerable<ISearchEngine> FilesEngines(ILogger? logger = null)
    {
        yield return AnnasArchive(logger);
        yield return Kickass(logger);
        yield return Nyaa(logger);
        yield return PirateBay(logger);
        yield return ZLibrary(logger);
        yield return _1337x(logger);
    }

    public static IEnumerable<ISearchEngine> AppsEngines(ILogger? logger = null)
    {
        yield return AppleAppStore(logger);
    }

    public static IEnumerable<ISearchEngine> ITEngines(ILogger? logger = null)
    {
        yield return ArchLinux(logger);
        yield return Elasticsearch(logger);
        yield return GitHub(logger);
        yield return GitLab(logger);
        yield return HackerNews(logger);
        yield return HuggingFace(logger);
        yield return MicrosoftLearn(logger);
        yield return NVD(logger);
        yield return Ollama(logger);
        yield return SourceHut(logger);
        yield return StackExchange(logger);
        yield return Steam(logger);
    }

    public static IEnumerable<ISearchEngine> ScienceEngines(ILogger? logger = null)
    {
        yield return Arxiv(logger);
        yield return GoogleScholar(logger);
        yield return OpenAlex(logger);
        yield return Pubmed(logger);
        yield return SemanticScholar(logger);
        yield return Wikipedia(logger);
        yield return WolframAlpha(logger);
    }

    public static IEnumerable<ISearchEngine> GeneralEngines(ILogger? logger = null)
    {
        yield return Ask(logger);
        yield return Baidu(logger);
        yield return Bing(logger);
        yield return Bpb(logger);
        yield return Brave(logger);
        yield return DuckDuckGo(logger);
        yield return Duden(logger);
        yield return Emojipedia(logger);
        yield return Geizhals(logger);
        yield return Google(logger);
        yield return Mediawiki(logger);
        yield return Mojeek(logger);
        yield return Mwmbl(logger);
        yield return OpenMeteo(logger);
        yield return Qwant(logger);
        yield return Searx(logger);
        yield return Seznam(logger);
        yield return Sogou(logger);
        yield return Startpage(logger);
        yield return Wordnik(logger);
        yield return Wttr(logger);
        yield return Yahoo(logger);
        yield return Yandex(logger);
    }

    public static IEnumerable<ISearchEngine> MusicEngines(ILogger? logger = null)
    {
        yield return Bandcamp(logger);
        yield return Deezer(logger);
        yield return Fyyd(logger);
        yield return Genius(logger);
        yield return Mixcloud(logger);
        yield return PodcastIndex(logger);
        yield return SoundCloud(logger);
        yield return Spotify(logger);
        yield return YandexMusic(logger);
    }

    public static IEnumerable<ISearchEngine> VideosEngines(ILogger? logger = null)
    {
        yield return Bilibili(logger);
        yield return BingVideos(logger);
        yield return Bitchute(logger);
        yield return Dailymotion(logger);
        yield return GoogleVideos(logger);
        yield return Ina(logger);
        yield return Invidious(logger);
        yield return Niconico(logger);
        yield return Odysee(logger);
        yield return PeerTube(logger);
        yield return Piped(logger);
        yield return Rumble(logger);
        yield return Vimeo(logger);
        yield return YouTube(logger);
    }

    public static IEnumerable<ISearchEngine> ImagesEngines(ILogger? logger = null)
    {
        yield return ArtStation(logger);
        yield return BingImages(logger);
        yield return DeviantArt(logger);
        yield return FindThatMeme(logger);
        yield return Flickr(logger);
        yield return Frinkiac(logger);
        yield return GoogleImages(logger);
        yield return Imgur(logger);
        yield return Ipernity(logger);
        yield return Lucide(logger);
        yield return MaterialIcons(logger);
        yield return OpenClipart(logger);
        yield return Openverse(logger);
        yield return Pexels(logger);
        yield return Pinterest(logger);
        yield return Pixabay(logger);
        yield return Pixiv(logger);
        yield return Unsplash(logger);
        yield return Wallhaven(logger);
        yield return WikiCommons(logger);
    }

    public static IEnumerable<ISearchEngine> NewsEngines(ILogger? logger = null)
    {
        yield return BingNews(logger);
        yield return GoogleNews(logger);
        yield return IlPost(logger);
        yield return Reuters(logger);
        yield return YahooNews(logger);
    }

    public static IEnumerable<ISearchEngine> PackagesEngines(ILogger? logger = null)
    {
        yield return AlpineLinux(logger);
        yield return Crates(logger);
        yield return DockerHub(logger);
        yield return FDroid(logger);
        yield return Hex(logger);
        yield return MetaCPAN(logger);
        yield return Npm(logger);
        yield return NuGet(logger);
        yield return PkgGoDev(logger);
        yield return Pypi(logger);
    }

    public static IEnumerable<ISearchEngine> SocialMediaEngines(ILogger? logger = null)
    {
        yield return Discourse(logger);
        yield return Lemmy(logger);
        yield return Mastodon(logger);
        yield return NineGag(logger);
        yield return Reddit(logger);
    }

    public static IEnumerable<ISearchEngine> ShoppingEngines(ILogger? logger = null)
    {
        yield return Ebay(logger);
        yield return Imdb(logger);
    }

    public static IEnumerable<ISearchEngine> BooksEngines(ILogger? logger = null)
    {
        yield return Goodreads(logger);
        yield return OpenLibrary(logger);
    }

    public static IEnumerable<ISearchEngine> MapEngines(ILogger? logger = null)
    {
        yield return OpenStreetMap(logger);
        yield return Photon(logger);
    }
}
