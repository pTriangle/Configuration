﻿//
// Copyright 2015 the original author or authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.IO;
using System.Net;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using Microsoft.AspNet.Hosting;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Net.Security;

namespace Spring.Extensions.Configuration.Common
{

    public class ConfigServerConfigurationProviderBase : ConfigurationProvider
    {

        private static readonly TimeSpan DEFAULT_TIMEOUT = new TimeSpan(0,0,5);
        protected ConfigServerClientSettingsBase _settings;
        protected HttpClient _client;
        protected ILogger _logger;
       

        internal protected ConfigServerConfigurationProviderBase(ConfigServerClientSettingsBase settings, ILoggerFactory logFactory = null) :
            this(settings, GetHttpClient(settings), logFactory)
        {
            _client.Timeout = DEFAULT_TIMEOUT;
        }


        internal protected ConfigServerConfigurationProviderBase(ConfigServerClientSettingsBase settings, HttpClient httpClient, ILoggerFactory logFactory = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            _logger = logFactory?.CreateLogger<ConfigServerConfigurationProviderBase>();
            _settings = settings;
            _client = httpClient;
        }

 
        /// <summary>
        /// Loads configuration data from the Spring Cloud Configuration Server as specified by
        /// the <see cref="Settings"/> 
        /// </summary>
        public override void Load()
        {
            // Adds client settings (e.g spring:cloud:config:uri) to the Data dictionary
            AddConfigServerClientSettings(_settings);

            var path = GetConfigServerUri();
            Task<Environment> task = RemoteLoadAsync(path);
            task.Wait();
            Environment env = task.Result;
            if (env != null)
            {
                _logger?.LogInformation("Located environment: {0}, {1}, {2}, {3}", env.Name, env.Profiles, env.Label, env.Version);
                var sources = env.PropertySources;
                if (sources != null)
                {

                    foreach (PropertySource source in sources)
                    {
                        AddPropertySource(source);
                    }
                }
            }
        }

        internal IDictionary<string, string> Properties
        {
            get
            {
                return Data;
            }
        }

        internal ILogger Logger
        {
            get
            {
                return _logger;
            }
        }

        internal virtual async Task<Environment> RemoteLoadAsync(string path)
        {
            var request = GetRequestMessage(path);

#if NET451
            RemoteCertificateValidationCallback prevValidator = null;
            if (!_settings.ValidateCertificates)
            {
                prevValidator = ServicePointManager.ServerCertificateValidationCallback;
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
            }
#endif

            try
            {
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        _logger?.LogInformation("Config Server returned status: {0} invoking path: {1}", 
                            response.StatusCode, path);
                        return null;
                    }

                    Stream stream = await response.Content.ReadAsStreamAsync();
                    return Deserialize(stream);
                }
            } catch (Exception e)
            {
                _logger?.LogError("Config Server exception: {0}, path: {1}", e, path);
            }
#if NET451
            finally
            {
                ServicePointManager.ServerCertificateValidationCallback = prevValidator;
            }
#endif

            return null;
        }
        internal virtual HttpRequestMessage GetRequestMessage(string path)
        {
            return new HttpRequestMessage(HttpMethod.Get, path);
        }

        internal virtual Environment Deserialize(Stream stream)
        {
            try {
                using (JsonReader reader = new JsonTextReader(new StreamReader(stream)))
                {
                    JsonSerializer serializer = new JsonSerializer();
                    return (Environment)serializer.Deserialize(reader, typeof(Environment));
                }
            } catch (Exception e)
            {
                _logger?.LogError("Config Server serialization exception", e);
            }
            return null;
        }

        internal virtual string GetConfigServerUri()
        {
            var path = "/" + _settings.Name + "/" + _settings.Environment;
            if (!string.IsNullOrWhiteSpace(_settings.Label))
                path = path + "/" + _settings.Label;

            return _settings.Uri + path;
        }

        internal virtual void AddPropertySource(PropertySource source)
        {
            if (source == null || source.Source == null)
                return;
    
            foreach(KeyValuePair<string,object> kvp in source.Source)
            {
                try {
                    string key = kvp.Key.Replace(".", Constants.KeyDelimiter);
                    string value = kvp.Value.ToString();
                    Data[key] = value;
                } catch (Exception e)
                {
                    _logger?.LogError("Config Server exception, property: {0}={1}", kvp.Key, kvp.Value.GetType(), e);
                }

            }
        }

        internal virtual void AddConfigServerClientSettings(ConfigServerClientSettingsBase settings)
        {
            Data["spring:cloud:config:enabled"] = settings.Enabled.ToString();
            Data["spring:cloud:config:failFast"] = settings.FailFast.ToString();
            Data["spring:cloud:config:env"] = settings.Environment;
            Data["spring:cloud:config:label"] = settings.Label;
            Data["spring:cloud:config:name"] = settings.Name;
            Data["spring:cloud:config:password"] = settings.Password;
            Data["spring:cloud:config:uri"] = settings.Uri;
            Data["spring:cloud:config:username"] = settings.Username;
            Data["spring:cloud:config:validate_certificates"] = settings.ValidateCertificates.ToString();
        }

        private static HttpClient GetHttpClient(ConfigServerClientSettingsBase settings)
        {
#if NET451
            return new HttpClient();
#else
            // TODO: For coreclr, disabling certificate validation only works on windows
            // https://github.com/dotnet/corefx/issues/4476
            if (settings != null && !settings.ValidateCertificates)
            {
                var handler = new WinHttpHandler();
                handler.ServerCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
                return new HttpClient(handler);
            } else
            {
                return new HttpClient();
            }
#endif
        }
    }
}