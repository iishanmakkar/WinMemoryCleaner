using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace WinMemoryCleaner
{
    public static class Localizer
    {
        #region Events

        public static event PropertyChangedEventHandler StaticPropertyChanged;

        #endregion

        #region Fields

        private static CultureInfo _culture;
        private static Language _language;
        private static Localization _fallbackLocalization;
        private static bool _isInitialized;

        #endregion

        #region Constructor

        static Localizer()
        {
            InitializeFallback();
            String = _fallbackLocalization;

            try
            {
                Culture = new CultureInfo(Settings.Language);
            }
            catch
            {
                Culture = new CultureInfo(Constants.Windows.Locale.Name.English);
            }

            Language = new Language(Culture);
            _isInitialized = true;
        }

        #endregion

        #region Properties

        public static CultureInfo Culture
        {
            get => _culture;
            private set
            {
                _culture = value;
                RaiseStaticPropertyChanged();
            }
        }

        public static Language Language
        {
            get => _language;
            set
            {
                if (_language != null && _language.Equals(value))
                    return;

                try
                {
                    if (value == null)
                        throw new ArgumentNullException(nameof(value));

                    Load(value);

                    Settings.Language = value.Name;
                    Settings.Save();
                }
                catch
                {
                    Settings.Language = Constants.Windows.Locale.Name.English;
                    Settings.Save();
                    throw;
                }

                _language = value;
                RaiseStaticPropertyChanged(string.Empty);

                if (_isInitialized)
                    App.ReleaseMemory();
            }
        }

        public static List<Language> Languages
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var resourcePrefix = Constants.App.LocalizationResourcePath;
                    var resourceSuffix = Constants.App.EmbeddedResourcePathExtension;

                    var resourceNames = assembly.GetManifestResourceNames()
                        .Where(file => file.StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase) &&
                                     file.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase))
                        .Select(file => file[resourcePrefix.Length..^resourceSuffix.Length])
                        .OrderBy(file => file)
                        .ToList();

                    try
                    {
                        var searchDirectories = new[]
                        {
                            AppDomain.CurrentDomain.BaseDirectory,
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Localization"),
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Themes")
                        };

                        var localResources = new List<string>();
                        foreach (var dir in searchDirectories)
                        {
                            try
                            {
                                var files = Directory.GetFiles(dir,
                                    $"*{Constants.App.EmbeddedResourcePathExtension}", SearchOption.TopDirectoryOnly);
                                localResources.AddRange(files.Select(Path.GetFileNameWithoutExtension));
                            }
                            catch
                            {
                            }
                        }

                        if (localResources.Any())
                            resourceNames.AddRange(localResources.Distinct());
                    }
                    catch
                    {
                    }

                    return CultureInfo.GetCultures(CultureTypes.AllCultures)
                        .Where(culture => resourceNames.Contains(culture.EnglishName, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(culture => culture.EnglishName, StringComparer.InvariantCultureIgnoreCase)
                        .Select(culture => new Language(culture))
                        .ToList();
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    return new List<Language> { new Language(new CultureInfo(Constants.Windows.Locale.Name.English)) };
                }
            }
        }

        public static Localization String { get; private set; }

        #endregion

        #region Methods

        private static void InitializeFallback()
        {
            _fallbackLocalization = new Localization();
            var props = typeof(Localization).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (prop.PropertyType == typeof(string) && prop.CanWrite)
                {
                    var name = prop.Name;
                    prop.SetValue(_fallbackLocalization, name);
                }
            }
        }

        private static void Load(Language language)
        {
            Localization localization = null;
            var resourceName = $"{Constants.App.LocalizationResourcePath}{language.EnglishName}{Constants.App.EmbeddedResourcePathExtension}";

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var assembliesToTry = new[] { Assembly.GetExecutingAssembly(), Assembly.GetEntryAssembly() }.Distinct();

            foreach (var assembly in assembliesToTry)
            {
                if (assembly == null) continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        localization = JsonSerializer.Deserialize<Localization>(stream, options);
                        if (localization != null)
                        {
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Debug($"Failed to load embedded resource {resourceName} from {assembly.FullName}: {e.Message}");
                }
            }

            if (localization == null)
            {
                var searchDirectories = new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Localization"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Themes")
                };

                foreach (var dir in searchDirectories)
                {
                    var localResource = Path.Combine(dir, $"{language.EnglishName}{Constants.App.EmbeddedResourcePathExtension}");
                    try
                    {
                        if (File.Exists(localResource))
                        {
                            using var stream = File.OpenRead(localResource);
                            localization = JsonSerializer.Deserialize<Localization>(stream, options);
                            if (localization != null)
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Debug($"Failed to load local file {localResource}: {e.Message}");
                    }
                }
            }

            if (localization == null)
            {
                Logger.Warning($"Language file for {language.EnglishName} not found or invalid, using fallback");
                localization = _fallbackLocalization;
            }

            var nullOrEmptyStrings = localization
                .GetType()
                .GetProperties()
                .Where(pi => pi.PropertyType == typeof(string) && string.IsNullOrWhiteSpace((string)pi.GetValue(localization)))
                .Select(pi => pi.Name)
                .ToList();

            if (nullOrEmptyStrings.Any())
            {
                Logger.Warning($"Language file for {language.EnglishName} has missing values: {string.Join(", ", nullOrEmptyStrings)}, using fallback");
                localization = _fallbackLocalization;
            }

            Culture = new CultureInfo(language.Name);
            String = localization;
        }

        private static void RaiseStaticPropertyChanged(string propertyName = null)
        {
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}