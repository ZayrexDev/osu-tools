// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Alba.CsConsoleFormat;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace PerformanceCalculator.Profile
{
    [Command(Name = "profile", Description = "Computes the total performance (pp) of a profile.")]
    public class ProfileCommand : ApiCommand
    {
        [UsedImplicitly]
        [Argument(0, Name = "user", Description = "User ID is preferred, but username should also work.")]
        public string ProfileName { get; } = null!;

        [UsedImplicitly]
        [Option(Template = "-r|--ruleset:<ruleset-id>", Description = "The ruleset to compute the profile for.\n"
                                                                      + "Values: 0 - osu!, 1 - osu!taiko, 2 - osu!catch, 3 - osu!mania")]
        [AllowedValues("0", "1", "2", "3")]
        public int? Ruleset { get; }

        [UsedImplicitly]
        [Option(Template = "--beatmap-cache <directory>", Description = "Directory for cached .osu files (default: ./cache).")]
        public string? BeatmapCache { get; }

        private const int max_api_scores = 200;
        private const int max_api_scores_in_one_query = 100;

        public override void Execute()
        {
            var displayPlays = new List<UserPlayInfo>();

            var ruleset = LegacyHelper.GetRulesetFromLegacyID(Ruleset ?? 0);
            string rulesetApiName = LegacyHelper.GetRulesetShortNameFromId(Ruleset ?? 0);

            var userData = GetJsonFromApi<APIUser>($"users/{ProfileName}/{ruleset.ShortName}");

            var apiScores = new List<JObject>();
            var jsonSettings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };

            for (int i = 0; i < max_api_scores; i += max_api_scores_in_one_query)
            {
                var page = GetRawJsonFromApi($"users/{userData.Id}/scores/best?mode={rulesetApiName}&limit={max_api_scores_in_one_query}&offset={i}");
                apiScores.AddRange(JsonConvert.DeserializeObject<List<JObject>>(page, jsonSettings)!);
                Thread.Sleep(200);
            }

            foreach (var rawScore in apiScores)
            {
                var play = rawScore.ToObject<SoloScoreInfo>()!;
                var working = ProcessorWorkingBeatmap.FromFileOrId(play.BeatmapID.ToString(), BeatmapCache);

                Mod[] mods = play.Mods.Select(x => x.ToMod(ruleset)).ToArray();

                var scoreInfo = play.ToScoreInfo(mods, working.BeatmapInfo);
                scoreInfo.Ruleset = ruleset.RulesetInfo;

                var difficultyCalculator = ruleset.CreateDifficultyCalculator(working);
                var difficultyAttributes = difficultyCalculator.Calculate(scoreInfo.Mods);
                var performanceCalculator = ruleset.CreatePerformanceCalculator();

                var ppAttributes = performanceCalculator?.Calculate(scoreInfo, difficultyAttributes);
                var thisPlay = new UserPlayInfo
                {
                    Beatmap = working.BeatmapInfo,
                    ApiScore = rawScore,
                    LocalPP = ppAttributes?.Total ?? 0,
                    LocalStarRating = difficultyAttributes.StarRating,
                    PerformanceBreakdown = ppAttributes,
                    LivePP = play.PP ?? 0.0,
                    Mods = scoreInfo.Mods.Select(m => m.Acronym).ToArray(),
                    MissCount = play.Statistics.GetValueOrDefault(HitResult.Miss),
                    HitCounts = (scoreInfo.GetCount100() ?? 0, scoreInfo.GetCount300() ?? 0, scoreInfo.GetCount50() ?? 0),
                    Accuracy = scoreInfo.Accuracy * 100,
                    Combo = play.MaxCombo,
                    MaxCombo = difficultyAttributes.MaxCombo
                };

                displayPlays.Add(thisPlay);
            }

            var localOrdered = displayPlays.OrderByDescending(p => p.LocalPP).ToList();
            var liveOrdered = displayPlays.OrderByDescending(p => p.LivePP).ToList();

            double totalLocalPP = 0;
            double aim = 0, speed = 0, accuracy = 0, flashlight = 0, reading = 0;
            for (int i = 0; i < localOrdered.Count; i++)
            {
                var play = localOrdered[i];
                double weight = Math.Pow(0.95, i);
                totalLocalPP += weight * play.LocalPP;
                if (play.PerformanceBreakdown is OsuPerformanceAttributes attributes)
                {
                    aim += weight * attributes.Aim;
                    speed += weight * attributes.Speed;
                    accuracy += weight * attributes.Accuracy;
                    flashlight += weight * attributes.Flashlight;
                    reading += weight * attributes.Reading;
                }
            }
            double totalLivePP = (double)(userData.Statistics.PP ?? 0);

            // https://github.com/ppy/osu-queue-score-statistics/blob/842653412d66eef527f7b7067b7cf50e886de954/osu.Server.Queues.ScoreStatisticsProcessor/Helpers/UserTotalPerformanceAggregateHelper.cs#L36-L38
            // this might be slightly incorrect for some profiles due to the deduplication happening on the osu-queue-score-statistics side which we can't account for here
            double playcountBonusPP = (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.995, Math.Min(userData.BeatmapPlayCountsCount, 1000)));
            totalLocalPP += playcountBonusPP;

            double totalDiffPP = totalLocalPP - totalLivePP;

            if (OutputJson)
            {
                string json = JsonConvert.SerializeObject(new
                {
                    userData.Username,
                    LivePp = totalLivePP,
                    LocalPp = totalLocalPP,
                    PlaycountPp = playcountBonusPP,
                    WeightedPerformanceBreakdown = (Ruleset ?? 0) == 0 ? new { aim, speed, accuracy, flashlight, reading } : null,
                    Scores = localOrdered.Select(item => new
                    {
                        LiveId = item.ApiScore["id"],
                        EndedAt = item.ApiScore["ended_at"],
                        Weight = item.ApiScore["weight"],
                        BeatmapId = item.Beatmap.OnlineID,
                        BeatmapName = item.Beatmap.ToString(),
                        item.Combo,
                        Rank = item.ApiScore["rank"],
                        item.Accuracy,
                        item.MissCount,
                        item.HitCounts.Count100,
                        item.HitCounts.Count300,
                        item.HitCounts.Count50,
                        item.Mods,
                        LivePp = item.LivePP,
                        LocalPp = item.LocalPP,
                        item.LocalStarRating,
                        item.PerformanceBreakdown,
                        PositionChange = liveOrdered.IndexOf(item) - localOrdered.IndexOf(item)
                    })
                });

                Console.Write(json);
            }
            else
            {
                OutputDocument(new Document(
                    new Span($"User:     {userData.Username}"), "\n",
                    new Span($"Live PP:  {totalLivePP:F1} (including {playcountBonusPP:F1}pp from playcount)"), "\n",
                    new Span($"Local PP: {totalLocalPP:F1} ({totalDiffPP:+0.0;-0.0;-})"), "\n",
                    new Grid
                    {
                        Columns =
                        {
                            GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto
                        },
                        Children =
                        {
                            new Cell("#"),
                            new Cell("beatmap"),
                            new Cell("max combo"),
                            new Cell("accuracy"),
                            new Cell("misses"),
                            new Cell("mods"),
                            new Cell("live pp"),
                            new Cell("local pp"),
                            new Cell("pp change"),
                            new Cell("position change"),
                            localOrdered.Select(item => new[]
                            {
                                new Cell($"{localOrdered.IndexOf(item) + 1}"),
                                new Cell($"{item.Beatmap.OnlineID} - {item.Beatmap}"),
                                new Cell($"{item.Combo}/{item.MaxCombo}x") { Align = Align.Right },
                                new Cell($"{Math.Round(item.Accuracy, 2)}%") { Align = Align.Right },
                                new Cell($"{item.MissCount}") { Align = Align.Right },
                                new Cell($"{(item.Mods.Length > 0 ? string.Join(", ", item.Mods) : "None")}") { Align = Align.Right },
                                new Cell($"{item.LivePP:F1}") { Align = Align.Right },
                                new Cell($"{item.LocalPP:F1}") { Align = Align.Right },
                                new Cell($"{item.LocalPP - item.LivePP:F1}") { Align = Align.Right },
                                new Cell($"{liveOrdered.IndexOf(item) - localOrdered.IndexOf(item):+0;-0;-}") { Align = Align.Center },
                            })
                        }
                    })
                );
            }
        }
    }
}
