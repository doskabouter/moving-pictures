﻿using System;
using System.Collections.Generic;
using System.Text;
using Cornerstone.ScraperEngine;
using System.IO;
using MediaPortal.Plugins.MovingPictures.Database;
using Cornerstone.Database;
using System.Windows.Forms;
using MediaPortal.Plugins.MovingPictures.Properties;
using MediaPortal.Plugins.MovingPictures.SignatureBuilders;
using MediaPortal.Plugins.MovingPictures.LocalMediaManagement;
using System.Reflection;
using System.Globalization;
using NLog;
using MediaPortal.Plugins.MovingPictures.LocalMediaManagement.MovieResources;
using MediaPortal.Configuration;

namespace MediaPortal.Plugins.MovingPictures.DataProviders {
    public class ScriptableProvider : IScriptableMovieProvider {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        #region Properties

        public string Name {
            get {
                return scraper.Name;
            }
        }

        public int ScriptID {
            get {
                return scraper.ID;
            }
        }


        public string Version {
            get { return scraper.Version; } 
        }

        public string Author {
            get { return scraper.Author; } 
        }

        public string Language {
            get {
                try {
                    return new CultureInfo(scraper.Language).DisplayName;
                }
                catch (ArgumentException) {
                    return "";
                }
            }
        }

        public string LanguageCode {
            get {
                return scraper.Language;
            }
        }

        public DateTime? Published {
            get {
                return scraper.Published;
            }
        }

        public bool DebugMode { 
            get {
                return scraper.DebugMode;
            }
            set {
                scraper.DebugMode = value;
            }
        }

        public string Description {
            get { return scraper.Description; } 
        }

        public bool ProvidesMoviesDetails {
            get { return providesMovieDetails; }
        }
        private bool providesMovieDetails = false;

        public bool ProvidesCoverArt {
            get { return providesCoverArt; }
        }
        private bool providesCoverArt = false;

        public bool ProvidesBackdrops {
            get { return providesBackdrops; }
        }
        private bool providesBackdrops = false;

        public ScriptableScraper Scraper {
            get { return scraper; }
        }
        private ScriptableScraper scraper = null;

        #endregion

        #region Public Methods

        public ScriptableProvider() {
        }

        public bool Load(string script) {
            bool debugMode = MovingPicturesCore.Settings.DataSourceDebugActive;
            scraper = new ScriptableScraper(script, debugMode);

            if (!scraper.LoadSuccessful) {
                scraper = null;
                return false;
            }

            providesMovieDetails = scraper.ScriptType.Contains("MovieDetailsFetcher");
            providesCoverArt = scraper.ScriptType.Contains("MovieCoverFetcher");
            providesBackdrops = scraper.ScriptType.Contains("MovieBackdropFetcher");

            return true;
        }

        public List<DBMovieInfo> Get(MovieSignature movieSignature) {
            if (scraper == null)
                return null;

            List<DBMovieInfo> rtn = new List<DBMovieInfo>();
            Dictionary<string, string> paramList = new Dictionary<string, string>();
            Dictionary<string, string> results;

            if (movieSignature.Title != null) paramList["search.title"] = movieSignature.Title;
            if (movieSignature.Keywords != null) paramList["search.keywords"] = movieSignature.Keywords;
            if (movieSignature.Year != null) paramList["search.year"] = movieSignature.Year.ToString();
            if (movieSignature.ImdbId != null) paramList["search.imdb_id"] = movieSignature.ImdbId;
            if (movieSignature.DiscId != null) paramList["search.disc_id"] = movieSignature.DiscId;
            if (movieSignature.MovieHash != null) paramList["search.moviehash"] = movieSignature.MovieHash;
            if (movieSignature.Path != null) paramList["search.basepath"] = movieSignature.Path;
            if (movieSignature.Folder != null) paramList["search.foldername"] = movieSignature.Folder;
            if (movieSignature.File != null) paramList["search.filename"] = movieSignature.File;
            
            //set higher level settings for script to use
            paramList["settings.defaultuseragent"] = MovingPicturesCore.Settings.UserAgent;
            paramList["settings.mepo_data"] = Config.GetFolder(Config.Dir.Config);

            // this variable is the filename without extension (and a second one without stackmarkers)
            if (!String.IsNullOrEmpty(movieSignature.File)) {
                paramList["search.filename_noext"] = Path.GetFileNameWithoutExtension(movieSignature.File);
                paramList["search.clean_filename"] = Utility.GetFileNameWithoutExtensionAndStackMarkers(movieSignature.File);
            }

            results = scraper.Execute("search", paramList);
            if (results == null) {
                logger.Error(Name + " scraper script failed to execute \"search\" node.");
                return rtn;
            }

            int count = 0;
            // The movie result is only valid if the script supplies a unique site
            while (results.ContainsKey("movie[" + count + "].site_id")) {
                string siteId;
                string prefix = "movie[" + count + "].";
                count++;

                // if the result does not yield a site id it's not valid so skip it
                if (!results.TryGetValue(prefix + "site_id", out siteId))
                    continue;

                // if this movie was already added skip it
                if (rtn.Exists(delegate(DBMovieInfo item) { return item.GetSourceMovieInfo(ScriptID).Identifier == siteId; }))
                    continue;

                // if this movie does not have a valid title, don't bother
                if (!results.ContainsKey(prefix + "title"))
                    continue;
                
                // We passed all checks so create a new movie object
                DBMovieInfo newMovie = new DBMovieInfo();

                // store the site id in the new movie object
                newMovie.GetSourceMovieInfo(ScriptID).Identifier = siteId;

                // Try to store all other fields in the new movie object
                foreach (DBField currField in DBField.GetFieldList(typeof(DBMovieInfo))) {
                    string value;
                    if (results.TryGetValue(prefix + currField.FieldName, out value))
                        currField.SetValue(newMovie, value.Trim());
                }

                // add the movie to our movie results list
                rtn.Add(newMovie);              
            }

            return rtn;
        }

        public UpdateResults Update(DBMovieInfo movie) {
            if (scraper == null)
                return UpdateResults.FAILED;

            Dictionary<string, string> paramList = new Dictionary<string, string>();
            Dictionary<string, string> results;
            bool hasSiteId = false;

            // try to load the id for the movie for this script
            // if we have no site id still continue as we might still
            // be able to grab details using another identifier such as imdb_id
            DBSourceMovieInfo idObj = movie.GetSourceMovieInfo(ScriptID);
            if (idObj != null && idObj.Identifier != null) {
                paramList["movie.site_id"] = idObj.Identifier;
                hasSiteId = true;
            }

            // load params
            foreach (DBField currField in DBField.GetFieldList(typeof(DBMovieInfo))) {
                if (currField.AutoUpdate && currField.GetValue(movie) != null)
                    paramList["movie." + currField.FieldName] = currField.GetValue(movie).ToString().Trim();
            }

            //set higher level settings for script to use
            paramList["settings.defaultuseragent"] = MovingPicturesCore.Settings.UserAgent;
            paramList["settings.mepo_data"] = Config.GetFolder(Config.Dir.Config);

            // try to retrieve results
            results = scraper.Execute("get_details", paramList);
            if (results == null) {
                logger.Error(Name + " scraper script failed to execute \"get_details\" node.");
                return UpdateResults.FAILED;
            }            
            
            if (!hasSiteId) {
                // if we started out without a site id
                // try to get it from the details response
                string siteId;
                if (results.TryGetValue("movie.site_id", out siteId))
                    movie.GetSourceMovieInfo(ScriptID).Identifier = siteId;
                else
                    // still no site id, so we are returning
                    return UpdateResults.FAILED_NEED_ID;
            }

            // get our new movie details
            DBMovieInfo newMovie = new DBMovieInfo();
            foreach (DBField currField in DBField.GetFieldList(typeof(DBMovieInfo))) {
                string value;
                bool success = results.TryGetValue("movie." + currField.FieldName, out value);

                if (success && value.Trim().Length > 0)
                    currField.SetValue(newMovie, value.Trim());
            }

            // and update as necessary
            movie.CopyUpdatableValues(newMovie);

            return UpdateResults.SUCCESS;
        }

        public bool GetArtwork(DBMovieInfo movie) {
            if (scraper == null)
                return false;

            Dictionary<string, string> paramList = new Dictionary<string, string>();
            Dictionary<string, string> results;

            // grab coverart loading settings
            int maxCovers = MovingPicturesCore.Settings.MaxCoversPerMovie;
            int maxCoversInSession = MovingPicturesCore.Settings.MaxCoversPerSession;

            // if we have already hit our limit for the number of covers to load, quit
            if (movie.AlternateCovers.Count >= maxCovers)
                return true;

            // try to load the id for the movie for this script
            DBSourceMovieInfo idObj = movie.GetSourceMovieInfo(ScriptID);
            if (idObj != null && idObj.Identifier != null)
                paramList["movie.site_id"] = idObj.Identifier;

            // load params for scraper
            foreach (DBField currField in DBField.GetFieldList(typeof(DBMovieInfo)))
                if (currField.GetValue(movie) != null)
                    paramList["movie." + currField.FieldName] = currField.GetValue(movie).ToString().Trim();

            //set higher level settings for script to use
            paramList["settings.defaultuseragent"] = MovingPicturesCore.Settings.UserAgent;
            paramList["settings.mepo_data"] = Config.GetFolder(Config.Dir.Config);

            // run the scraper
            results = scraper.Execute("get_cover_art", paramList);
            if (results == null) {
                logger.Error(Name + " scraper script failed to execute \"get_cover_art\" node.");
                return false;
            }

            int coversAdded = 0;
            int count = 0;
            while (results.ContainsKey("cover_art[" + count + "].url") || results.ContainsKey("cover_art[" + count + "].file")) {
                // if we have hit our limit quit
                if (movie.AlternateCovers.Count >= maxCovers || coversAdded >= maxCoversInSession)
                    return true;

                // get url for cover and load it via the movie object
                if (results.ContainsKey("cover_art[" + count + "].url")) {
                    string coverPath = results["cover_art[" + count + "].url"];
                    if (coverPath.Trim() != string.Empty)
                        if (movie.AddCoverFromURL(coverPath) == ImageLoadResults.SUCCESS)
                            coversAdded++;
                }

                // get file for cover and load it via the movie object
                if (results.ContainsKey("cover_art[" + count + "].file")) {
                    string coverPath = results["cover_art[" + count + "].file"];
                    if (coverPath.Trim() != string.Empty)
                        if (movie.AddCoverFromFile(coverPath))
                            coversAdded++;
                }

                count++;
            }

            if (coversAdded > 0)
                return true;

            return false;
        }

        public bool GetBackdrop(DBMovieInfo movie) {
            if (scraper == null)
                return false;

            Dictionary<string, string> paramList = new Dictionary<string, string>();
            Dictionary<string, string> results;

            // if we already have a backdrop move on for now
            if (movie.BackdropFullPath.Trim().Length > 0)
                return true;

            // try to load the id for the movie for this script
            DBSourceMovieInfo idObj = movie.GetSourceMovieInfo(ScriptID);
            if (idObj != null && idObj.Identifier != null)
                paramList["movie.site_id"] = idObj.Identifier;

            // load params for scraper
            foreach (DBField currField in DBField.GetFieldList(typeof(DBMovieInfo)))
                if (currField.GetValue(movie) != null)
                    paramList["movie." + currField.FieldName] = currField.GetValue(movie).ToString().Trim();

            //set higher level settings for script to use
            paramList["settings.defaultuseragent"] = MovingPicturesCore.Settings.UserAgent;
            paramList["settings.mepo_data"] = Config.GetFolder(Config.Dir.Config);

            // run the scraper
            results = scraper.Execute("get_backdrop", paramList);
            if (results == null) {
                logger.Error(Name + " scraper script failed to execute \"get_backdrop\" node.");
                return false;
            }


            // Loop through all the results until a valid backdrop is found
            int count = 0;
            while (results.ContainsKey("backdrop[" + count + "].url") || results.ContainsKey("backdrop[" + count + "].file")) {

                // attempt to load via a URL
                if (results.ContainsKey("backdrop[" + count + "].url")) {
                    string backdropURL = results["backdrop[" + count + "].url"];
                    if (backdropURL.Trim().Length > 0)
                        if (movie.AddBackdropFromURL(backdropURL) == ImageLoadResults.SUCCESS)
                            return true;
                }

                // attempt to load via a file
                if (results.ContainsKey("backdrop[" + count + "].file")) {
                    string backdropFile = results["backdrop[" + count + "].file"];
                    if (backdropFile.Trim().Length > 0)
                        if (movie.AddBackdropFromFile(backdropFile))
                            return true;
                }

                count++;
            }
            
            // no valid backdrop found
            return false;
        }

        public override bool Equals(object obj) {
            if (obj == null) return false;

            if (obj.GetType() != typeof(ScriptableProvider))
                return base.Equals(obj);

            return Version.Equals(((ScriptableProvider)obj).Version) &&
                   Scraper.ID == ((ScriptableProvider)obj).Scraper.ID;
        }

        public override int GetHashCode() {
            return (Version + Scraper.ID).GetHashCode();
        }


        #endregion
    }
}
