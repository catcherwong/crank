// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Crank.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net.Http;
using System.Text;
using System.Net;
using System.Linq;

namespace Microsoft.Crank.Controller.Serializers
{
    public class JobSerializer
    {
        public static Task WriteJobResultsToSqlAsync(
            JobResults jobResults, 
            string sqlConnectionString, 
            string tableName,
            string session,
            string scenario,
            string description
            )
        {
            var utcNow = DateTime.UtcNow;

            var document = JsonConvert.SerializeObject(jobResults, Formatting.None, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });

            return RetryOnExceptionAsync(5, () =>
                 WriteResultsToSql(
                    utcNow,
                    sqlConnectionString,
                    tableName,
                    session,
                    scenario,
                    description,
                    document
                    )
                , 5000);
        }

        public static async Task InitializeDatabaseAsync(string connectionString, string tableName)
        {
            var createCmd =
                @"
                IF OBJECT_ID(N'dbo." + tableName + @"', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[" + tableName + @"](
                        [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        [Excluded] [bit] DEFAULT 0,
                        [DateTimeUtc] [datetimeoffset](7) NOT NULL,
                        [Session] [nvarchar](200) NOT NULL,
                        [Scenario] [nvarchar](200) NOT NULL,
                        [Description] [nvarchar](200) NOT NULL,
                        [Document] [nvarchar](max) NOT NULL
                    )
                END
                ";

            await RetryOnExceptionAsync(5, () => InitializeDatabaseInternalAsync(connectionString, createCmd), 5000);

            static async Task InitializeDatabaseInternalAsync(string connectionString, string createCmd)
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand(createCmd, connection))
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        private static async Task WriteResultsToSql(
            DateTime utcNow,
            string connectionString,
            string tableName,
            string session,
            string scenario,
            string description,
            string document
            )
        {

            var insertCmd =
                @"
                INSERT INTO [dbo].[" + tableName + @"]
                           ([DateTimeUtc]
                           ,[Session]
                           ,[Scenario]
                           ,[Description]
                           ,[Document])
                     VALUES
                           (@DateTimeUtc
                           ,@Session
                           ,@Scenario
                           ,@Description
                           ,@Document)
                ";

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                var transaction = connection.BeginTransaction();

                try
                {
                    var command = new SqlCommand(insertCmd, connection, transaction);
                    var p = command.Parameters;
                    p.AddWithValue("@DateTimeUtc", utcNow);
                    p.AddWithValue("@Session", session);
                    p.AddWithValue("@Scenario", scenario ?? "");
                    p.AddWithValue("@Description", description ?? "");
                    p.AddWithValue("@Document", document);

                    await command.ExecuteNonQueryAsync();

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
                finally
                {
                    transaction.Dispose();
                }
            }
        }
 public static Task WriteJobResultsToEsAsync(
                 JobResults jobResults,
                 string elasticSearchUrl,
                 string indexName,
                 string session,
                 string scenario,
                 string description
                 )
      {
          var utcNow = DateTime.UtcNow;
          return RetryOnExceptionAsync(5, () =>
               WriteResultsToEs(
                  utcNow,
                  elasticSearchUrl,
                  indexName,
                  session,
                  scenario,
                  description,
                  jobResults
                  )
              , 5000);
      }

      public static async Task InitializeElasticSearchAsync(string elasticSearchUrl, string indexName)
      {
          var mappingQuery =
              @"{""settings"": {""number_of_shards"": 1},""mappings"": { ""dynamic"": false, 
              ""properties"": {
              ""DateTimeUtc"": { ""type"": ""date"" },
              ""Session"": { ""type"": ""keyword"" },
              ""Scenario"": { ""type"": ""keyword"" },
              ""Description"": { ""type"": ""keyword"" },
              ""Document"": { ""type"": ""nested"" }}}}";

          await RetryOnExceptionAsync(5, () => InitializeDatabaseInternalAsync(elasticSearchUrl,indexName, mappingQuery), 5000);

          static async Task InitializeDatabaseInternalAsync(string elasticSearchUrl,string indexName, string mappingQuery)
          {
              using (var httpClient = new HttpClient())
              {
                  HttpResponseMessage hrm = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{elasticSearchUrl}/{indexName.ToLower()}"));
                  if (hrm.StatusCode == HttpStatusCode.NotFound)
                  {
                      hrm = await httpClient.PutAsync($"{elasticSearchUrl}/{indexName.ToLower()}", new StringContent(mappingQuery, Encoding.UTF8, "application/json"));
                      if (hrm.StatusCode != HttpStatusCode.OK)
                      {
                          throw new System.Exception(await hrm.Content.ReadAsStringAsync());
                      }
                  }
              }
          }
      }

      private static async Task WriteResultsToEs(
          DateTime utcNow,
          string elasticSearchUrl,
          string indexName,
          string session,
          string scenario,
          string description,
          JobResults jobResults
          )
      {
          var result = new
          {
              DateTimeUtc = utcNow,
              Session = session,
              Scenario = scenario,
              Description = description,
              Document = jobResults
          };

          using (var httpClient = new HttpClient())
          {
              var item = JsonConvert.SerializeObject(result, Formatting.None, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });
              HttpResponseMessage hrm = await httpClient.PostAsync(elasticSearchUrl+"/"+indexName.ToLower() + "/_doc/" + session, new StringContent(item.ToString(), Encoding.UTF8, "application/json"));
              if (!hrm.IsSuccessStatusCode)
              {
                  throw new Exception(hrm.RequestMessage?.ToString());
              }
          }
      }

        private async static Task RetryOnExceptionAsync(int retries, Func<Task> operation, int milliSecondsDelay = 0)
        {
            var attempts = 0;
            do
            {
                try
                {
                    attempts++;
                    await operation();
                    return;
                }
                catch (Exception e)
                {
                    if (attempts == retries + 1)
                    {
                        throw;
                    }

                    Log($"Attempt {attempts} failed: {e.Message}");

                    if (milliSecondsDelay > 0)
                    {
                        await Task.Delay(milliSecondsDelay);
                    }
                }
            } while (true);
        }

        private static void Log(string message)
        {
            var time = DateTime.Now.ToString("hh:mm:ss.fff");
            Console.WriteLine($"[{time}] {message}");
        }

        public static async Task WriteJobResultsToInfluxDbAsync(JobResults jobResults, string influxDbUrl, string influxDbApiToken, string influxDbOrgId, string influxDbBucket, string session, string scenario, string description)
        {
            var data = GenLineProtocolData(jobResults, session, scenario);

            await RetryOnExceptionAsync(5, () => WriteResultsToInfluxDb(influxDbUrl, influxDbApiToken, influxDbOrgId, influxDbBucket, data), 5000);

            static async Task WriteResultsToInfluxDb(string influxDbUrl, string influxDbApiToken, string influxDbOrgId, string influxDbBucket, string data)
            {
                var reqUrl = $"{influxDbUrl}/api/v2/write?orgID={influxDbOrgId}&bucket={influxDbBucket}";
                using HttpClient client = new();
                StringContent content = new(data);
                HttpRequestMessage reqMsg = new(HttpMethod.Post, reqUrl);
                reqMsg.Content = content;
                reqMsg.Headers.TryAddWithoutValidation("Authorization", $"Token {influxDbApiToken}");
                var resp = await client.SendAsync(reqMsg);
                resp.EnsureSuccessStatusCode();
            }

            static string GenLineProtocolData(JobResults results, string session, string scenario)
            {
                // the value of these measurements can be ignore
                string[] ignoreMeasurements = new string[] { "bombardier/raw", "netSdkVersion", "AspNetCoreVersion", "NetCoreAppVersion" };
                var data = new StringBuilder();

                foreach (var job in results.Jobs)
                {
                    var key = job.Key;
                    object netSdkVer = "";
                    job.Value.Results.TryGetValue("netSdkVersion", out netSdkVer);
                    object aspNetCoreVer = "";
                    job.Value.Results.TryGetValue("AspNetCoreVersion", out aspNetCoreVer);
                    object netCoreAppVer = "";
                    job.Value.Results.TryGetValue("NetCoreAppVersion", out netCoreAppVer);
                    object hw = "";
                    job.Value.Environment.TryGetValue("hw", out hw);
                    object env = "";
                    job.Value.Environment.TryGetValue("env", out env);
                    object os = "";
                    job.Value.Environment.TryGetValue("os", out os);
                    object arch = "";
                    job.Value.Environment.TryGetValue("arch", out arch);
                    object proc = "";
                    job.Value.Environment.TryGetValue("proc", out proc);

                    foreach (var measurements in job.Value.Measurements)
                    {
                        foreach (var measurement in measurements)
                        {
                            if (!ignoreMeasurements.Contains(measurement.Name))
                            {
                                // Measurement
                                data.Append($"{key}.{measurement.Name},");

                                // Tag set, session and scenario
                                data.Append($"session={session},scenario={scenario},");

                                // Tag set, sdk info
                                if (netSdkVer != null && !string.IsNullOrWhiteSpace(netSdkVer.ToString()))
                                {
                                    data.Append($"netSdkVersion={netSdkVer},AspNetCoreVersion={aspNetCoreVer},NetCoreAppVersion={netCoreAppVer},");
                                }

                                // Tag set, environment
                                data.Append($"hw={hw},env={env},os={os},arch={arch},proc={proc}");

                                // Field set, value
                                data.Append($" value={measurement.Value}");

                                // Timestamp
                                data.Append($" {GetTimestamp(measurement.Timestamp)}\n");
                            }
                        }
                    }
                }

                return data.ToString();
            }

            static string GetTimestamp(DateTime datetime)
            {
                var ts = datetime.Subtract(DateTime.UnixEpoch).Ticks * 100;
                return ts.ToString();
            }
        }

        public static async Task InitializeInfluxDbAsync(string influxDbUrl, string influxDbApiToken, string influxDbOrgId, string influxDbBucket)
        {
            await RetryOnExceptionAsync(5, () => InitializeInfluxDbInternalAsync(influxDbUrl, influxDbApiToken, influxDbOrgId, influxDbBucket), 5000);

            static async Task InitializeInfluxDbInternalAsync(string influxDbUrl, string influxDbApiToken, string influxDbOrgId, string influxDbBucket)
            {
                var reqUrl = $"{influxDbUrl}/api/v2/buckets?orgID={influxDbOrgId}&name={influxDbBucket}";
                using HttpClient client = new();
                HttpRequestMessage httpReq = new(HttpMethod.Get, reqUrl);
                httpReq.Headers.TryAddWithoutValidation("Authorization", $"Token {influxDbApiToken}");
                var resp = await client.SendAsync(httpReq);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    Log($"Not Found influxdb bucket {influxDbBucket}, should create");
                    await CreateBucket(influxDbUrl, influxDbApiToken, influxDbOrgId, influxDbBucket);
                }
            }

            static async Task CreateBucket(string influxDbUrl, string influxDbApiToken, string influxDbOrgId, string influxDbBucket)
            {
                var reqUrl = $"{influxDbUrl}/api/v2/buckets";
                using HttpClient client = new();
                var data = new { description = "Crank Benchmark", name = influxDbBucket, orgID = influxDbOrgId };
                StringContent content = new(JsonConvert.SerializeObject(data));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                HttpRequestMessage httpReq = new(HttpMethod.Post, reqUrl);
                httpReq.Content = content;
                httpReq.Headers.TryAddWithoutValidation("Authorization", $"Token {influxDbApiToken}");
                var resp = await client.SendAsync(httpReq);
                resp.EnsureSuccessStatusCode();
            }
        }
    }
}
