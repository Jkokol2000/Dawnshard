﻿using AutoMapper;
using DragaliaAPI.Database;
using DragaliaAPI.Database.Entities;
using DragaliaAPI.Models.Generated;
using DragaliaAPI.Shared.Definitions.Enums;
using DragaliaAPI.Shared.PlayerDetails;
using Microsoft.EntityFrameworkCore;

namespace DragaliaAPI.Services.Game;

public class UpdateDataService : IUpdateDataService
{
    private readonly ApiContext apiContext;
    private readonly IMapper mapper;
    private readonly IPlayerIdentityService playerIdentityService;

    public UpdateDataService(
        ApiContext apiContext,
        IMapper mapper,
        IPlayerIdentityService playerIdentityService
    )
    {
        this.apiContext = apiContext;
        this.mapper = mapper;
        this.playerIdentityService = playerIdentityService;
    }

    public async Task<UpdateDataList> SaveChangesAsync()
    {
        List<IDbHasAccountId> entities = this.apiContext.ChangeTracker
            .Entries<IDbHasAccountId>()
            .Where(
                x =>
                    (x.State is EntityState.Modified or EntityState.Added)
                    && x.Entity.DeviceAccountId == this.playerIdentityService.AccountId
            )
            .Select(x => x.Entity)
            .ToList();

        await this.apiContext.SaveChangesAsync();

        return this.MapUpdateDataList(entities);
    }

    private UpdateDataList MapUpdateDataList(List<IDbHasAccountId> entities) =>
        new()
        {
            user_data = this.ConvertEntities<UserData, DbPlayerUserData>(entities)?.Single(), // Can't use SingleOrDefault if the list itself is null
            chara_list = this.ConvertEntities<CharaList, DbPlayerCharaData>(entities),
            dragon_list = this.ConvertEntities<DragonList, DbPlayerDragonData>(entities),
            dragon_reliability_list = this.ConvertEntities<
                DragonReliabilityList,
                DbPlayerDragonReliability
            >(entities),
            weapon_body_list = this.ConvertEntities<WeaponBodyList, DbWeaponBody>(entities),
            weapon_skin_list = this.ConvertEntities<WeaponSkinList, DbWeaponSkin>(entities),
            ability_crest_list = this.ConvertEntities<AbilityCrestList, DbAbilityCrest>(entities),
            party_list = this.ConvertEntities<PartyList, DbParty>(entities),
            quest_story_list = this.ConvertEntities<QuestStoryList, DbPlayerStoryState>(
                entities,
                x => x.StoryType == StoryTypes.Quest
            ),
            unit_story_list = this.ConvertEntities<UnitStoryList, DbPlayerStoryState>(
                entities,
                x => x.StoryType == StoryTypes.Chara || x.StoryType == StoryTypes.Dragon
            ),
            castle_story_list = this.ConvertEntities<CastleStoryList, DbPlayerStoryState>(
                entities,
                x => x.StoryType == StoryTypes.Castle
            ),
            material_list = this.ConvertEntities<MaterialList, DbPlayerMaterial>(entities),
            dragon_gift_list = this.ConvertEntities<DragonGiftList, DbPlayerDragonGift>(
                entities,
                x => x.DragonGiftId > DragonGifts.GoldenChalice
            ),
            quest_list = this.ConvertEntities<QuestList, DbQuest>(entities),
            build_list = this.ConvertEntities<BuildList, DbFortBuild>(entities),
            weapon_passive_ability_list = this.ConvertEntities<
                WeaponPassiveAbilityList,
                DbWeaponPassiveAbility
            >(entities)
        };

    private List<TNetwork>? ConvertEntities<TNetwork, TDatabase>(
        IEnumerable<IDbHasAccountId> baseEntries,
        Func<TDatabase, bool>? filterPredicate = null
    )
        where TDatabase : IDbHasAccountId
    {
        IEnumerable<TDatabase> typedEntries = baseEntries.OfType<TDatabase>();

        if (filterPredicate is not null)
        {
            typedEntries = typedEntries.Where(filterPredicate);
        }

        return typedEntries.Any()
            ? typedEntries.Select(x => this.mapper.Map<TNetwork>(x)).ToList()
            : null;
    }
}
