﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using NadekoBot.Attributes;
using NadekoBot.Extensions;
using NadekoBot.Services;
using NadekoBot.Services.Database;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

//todo rewrite
namespace NadekoBot.Modules.Games
{
    public partial class Games
    {
        /// <summary>
        /// Flower picking/planting idea is given to me by its
        /// inceptor Violent Crumble from Game Developers League discord server
        /// (he has !cookie and !nom) Thanks a lot Violent!
        /// Check out GDL (its a growing gamedev community):
        /// https://discord.gg/0TYNJfCU4De7YIk8
        /// </summary>
        [Group]
        public class PlantPick
        {

            private Random rng;

            private ConcurrentDictionary<ulong, bool> generationChannels = new ConcurrentDictionary<ulong, bool>();
            //channelid/message
            private ConcurrentDictionary<ulong, List<IUserMessage>> plantedFlowers = new ConcurrentDictionary<ulong, List<IUserMessage>>();
            //channelId/last generation
            private ConcurrentDictionary<ulong, DateTime> lastGenerations = new ConcurrentDictionary<ulong, DateTime>();

            private float chance;
            private int cooldown;

            public PlantPick()
            {
                NadekoBot.Client.MessageReceived += PotentialFlowerGeneration;
                rng = new Random();

                using (var uow = DbHandler.UnitOfWork())
                {
                    var conf = uow.BotConfig.GetOrCreate();
                    var x =
                    generationChannels = new ConcurrentDictionary<ulong, bool>(uow.GuildConfigs.GetAll()
                        .Where(c => c.GenerateCurrencyChannelId != null)
                        .ToDictionary(c => c.GenerateCurrencyChannelId.Value, c => true));
                    chance = conf.CurrencyGenerationChance;
                    cooldown = conf.CurrencyGenerationCooldown;
                }
            }

            private Task PotentialFlowerGeneration(IMessage imsg)
            {
                var msg = imsg as IUserMessage;
                if (msg == null || msg.IsAuthor())
                    return Task.CompletedTask;

                var channel = imsg.Channel as ITextChannel;
                if (channel == null)
                    return Task.CompletedTask;

                bool shouldGenerate;
                if (!generationChannels.TryGetValue(channel.Id, out shouldGenerate) || !shouldGenerate)
                    return Task.CompletedTask;

                var t = Task.Run(async () =>
                {
                    var lastGeneration = lastGenerations.GetOrAdd(channel.Id, DateTime.MinValue);

                    if (DateTime.Now - TimeSpan.FromSeconds(cooldown) < lastGeneration) //recently generated in this channel, don't generate again
                        return;

                    var num = rng.Next(1, 101) + chance * 100;

                    if (num > 100)
                    {
                        lastGenerations.AddOrUpdate(channel.Id, DateTime.Now, (id, old) => DateTime.Now);
                        //todo get prefix
                        try
                        {
                            var sent = await channel.SendFileAsync(
                                GetRandomCurrencyImagePath(), 
                                $"❗ A random { Gambling.Gambling.CurrencyName } appeared! Pick it up by typing `>pick`")
                                    .ConfigureAwait(false);
                            plantedFlowers.AddOrUpdate(channel.Id, new List<IUserMessage>() { sent }, (id, old) => { old.Add(sent); return old; });
                        }
                        catch { }
                        
                    }
                });
                return Task.CompletedTask;
            }
            [LocalizedCommand, LocalizedDescription, LocalizedSummary, LocalizedAlias]
            [RequireContext(ContextType.Guild)]
            public async Task Pick(IUserMessage imsg)
            {
                var channel = (ITextChannel)imsg.Channel;

                if (!channel.Guild.GetCurrentUser().GetPermissions(channel).ManageMessages)
                {
                    await channel.SendMessageAsync("`I need manage channel permissions in order to process this command.`").ConfigureAwait(false);
                    return;
                }

                List<IUserMessage> msgs;

                await imsg.DeleteAsync().ConfigureAwait(false);
                if (!plantedFlowers.TryRemove(channel.Id, out msgs))
                    return;

                await Task.WhenAll(msgs.Select(toDelete => toDelete.DeleteAsync())).ConfigureAwait(false);

                await CurrencyHandler.AddCurrencyAsync((IGuildUser)imsg.Author, "Picked a flower.", 1, false).ConfigureAwait(false);
                var msg = await channel.SendMessageAsync($"**{imsg.Author.Username}** picked a {Gambling.Gambling.CurrencyName}!").ConfigureAwait(false);
                var t = Task.Run(async () =>
                 {
                     try
                     {
                         await Task.Delay(10000).ConfigureAwait(false);
                         await msg.DeleteAsync().ConfigureAwait(false);
                     }
                     catch { }
                 });
            }

            [LocalizedCommand, LocalizedDescription, LocalizedSummary, LocalizedAlias]
            [RequireContext(ContextType.Guild)]
            public async Task Plant(IUserMessage imsg)
            {
                var channel = (ITextChannel)imsg.Channel;
                if (channel == null)
                    return;

                var removed = await CurrencyHandler.RemoveCurrencyAsync((IGuildUser)imsg.Author, "Planted a flower.", 1, false).ConfigureAwait(false);
                if (!removed)
                {
                    await channel.SendMessageAsync($"You don't have any {Gambling.Gambling.CurrencyName}s.").ConfigureAwait(false);
                    return;
                }

                var file = GetRandomCurrencyImagePath();
                IUserMessage msg;
                var vowelFirst = new[] { 'a', 'e', 'i', 'o', 'u' }.Contains(Gambling.Gambling.CurrencyName[0]);
                var msgToSend = $"Oh how Nice! **{imsg.Author.Username}** planted {(vowelFirst ? "an" : "a")} {Gambling.Gambling.CurrencyName}. Pick it using >pick";
                if (file == null)
                {
                    msg = await channel.SendMessageAsync(Gambling.Gambling.CurrencySign).ConfigureAwait(false);
                }
                else
                {
                    //todo add prefix
                    msg = await channel.SendFileAsync(file, msgToSend).ConfigureAwait(false);
                }
                plantedFlowers.AddOrUpdate(channel.Id, new List<IUserMessage>() { msg }, (id, old) => { old.Add(msg); return old; });
            }
            
            [LocalizedCommand, LocalizedDescription, LocalizedSummary, LocalizedAlias]
            [RequireContext(ContextType.Guild)]
            [RequirePermission(GuildPermission.ManageMessages)]
            public async Task Gencurrency(IUserMessage imsg)
            {
                var channel = imsg.Channel as ITextChannel;
                if (channel == null)
                    return;

                bool enabled;
                using (var uow = DbHandler.UnitOfWork())
                {
                    var guildConfig = uow.GuildConfigs.For(channel.Id);

                    if (guildConfig.GenerateCurrencyChannelId == null)
                    {
                        guildConfig.GenerateCurrencyChannelId = channel.Id;
                        generationChannels.TryAdd(channel.Id, true);
                        enabled = true;
                    }
                    else
                    {
                        guildConfig.GenerateCurrencyChannelId = null;
                        bool throwaway;
                        generationChannels.TryRemove(channel.Id, out throwaway);
                        enabled = false;
                    }
                    await uow.CompleteAsync();
                }
                if (enabled)
                {
                    await channel.SendMessageAsync("`Currency generation enabled on this channel.`").ConfigureAwait(false);
                }
                else
                {
                    await channel.SendMessageAsync($"`Currency generation disabled on this channel.`").ConfigureAwait(false);
                }
            }

            private string GetRandomCurrencyImagePath() =>
                Directory.GetFiles("data/currency_images").OrderBy(s => rng.Next()).FirstOrDefault();

            int GetRandomNumber()
            {
                using (var rg = RandomNumberGenerator.Create())
                {
                    byte[] rno = new byte[4];
                    rg.GetBytes(rno);
                    int randomvalue = BitConverter.ToInt32(rno, 0);
                    return randomvalue;
                }
            }
        }
    }
}