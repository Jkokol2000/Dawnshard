﻿using AutoMapper;
using DragaliaAPI.Database.Entities;
using DragaliaAPI.Database.Repositories;
using DragaliaAPI.Models;
using DragaliaAPI.Models.Generated;
using DragaliaAPI.Services.Exceptions;
using DragaliaAPI.Shared.Definitions.Enums;
using DragaliaAPI.Shared.MasterAsset;
using DragaliaAPI.Shared.MasterAsset.Models;
using DragaliaAPI.Shared.PlayerDetails;
using Microsoft.EntityFrameworkCore;

namespace DragaliaAPI.Services.Game;

public class FortService : IFortService
{
    public const int MaximumCarpenterNum = 5;

    private readonly IFortRepository fortRepository;
    private readonly IInventoryRepository inventoryRepository;
    private readonly IUserDataRepository userDataRepository;
    private readonly ILogger<FortService> logger;
    private readonly IPlayerIdentityService playerIdentityService;
    private readonly IMapper mapper;

    public FortService(
        IFortRepository fortRepository,
        IUserDataRepository userDataRepository,
        IInventoryRepository inventoryRepository,
        ILogger<FortService> logger,
        IPlayerIdentityService playerIdentityService,
        IMapper mapper
    )
    {
        this.fortRepository = fortRepository;
        this.userDataRepository = userDataRepository;
        this.inventoryRepository = inventoryRepository;
        this.logger = logger;
        this.playerIdentityService = playerIdentityService;
        this.mapper = mapper;
    }

    public async Task<IEnumerable<BuildList>> GetBuildList()
    {
        return (await this.fortRepository.Builds.ToListAsync()).Select(mapper.Map<BuildList>);
    }

    public async Task<FortDetail> AddCarpenter(PaymentTypes paymentType)
    {
        DbPlayerUserData userData = await this.userDataRepository.UserData.SingleAsync();

        FortDetail fortDetail = await this.GetFortDetail();

        int paymentHeld = 0;
        // https://dragalialost.wiki/w/Facilities
        // First 2 are free, 3rd 250, 4th 400, 5th 700
        int paymentCost = fortDetail.carpenter_num switch
        {
            < 2 => 0,
            2 => 250,
            3 => 400,
            4 => 750,
            > 4
                => throw new DragaliaException(
                    ResultCode.FortExtendCarpenterLimit,
                    $"User has reached maximum carpenter."
                )
        };

        switch (paymentType)
        {
            case PaymentTypes.Wyrmite:
                paymentHeld = userData.Crystal;
                break;
            case PaymentTypes.Diamantium:
                // TODO How do I diamantium?
                break;
            default:
                throw new DragaliaException(
                    ResultCode.FortExtendCarpenterLimit,
                    $"Invalid currency used to add carpenter."
                );
        }

        if (paymentHeld < paymentCost)
        {
            throw new DragaliaException(
                ResultCode.FortExtendCarpenterLimit,
                $"User did not have enough {paymentType}."
            );
        }

        this.fortRepository.ConsumePaymentCost(userData, paymentType, paymentCost);

        // Add carpenter
        await this.fortRepository.UpdateFortMaximumCarpenter(fortDetail.carpenter_num + 1);

        this.logger.LogDebug(
            "Added carpenter using payment type {type}. New count: {count}",
            paymentType,
            fortDetail.carpenter_num
        );

        return fortDetail;
    }

    /// <summary>
    /// Get the fort details object.
    /// </summary>
    /// <remarks>
    /// Where this is used after changing fort state, it MUST be used after calling SaveChangesAsync to ensure
    /// the active carpenter count is accurate.
    /// </remarks>
    /// <returns></returns>
    public async Task<FortDetail> GetFortDetail()
    {
        DbFortDetail dbDetail = await this.fortRepository.GetFortDetail();
        int activeCarpenters = await this.fortRepository.GetActiveCarpenters();

        return new()
        {
            max_carpenter_count = MaximumCarpenterNum,
            working_carpenter_num = activeCarpenters,
            carpenter_num = dbDetail.CarpenterNum
        };
    }

    public async Task CompleteAtOnce(PaymentTypes paymentType, long buildId)
    {
        this.logger.LogDebug("CompleteAtOnce called for build {buildId}", buildId);

        DbPlayerUserData userData = await this.userDataRepository.UserData.SingleAsync();

        await this.fortRepository.UpgradeAtOnce(userData, buildId, paymentType);
    }

    public async Task<DbFortBuild> CancelUpgrade(long buildId)
    {
        this.logger.LogDebug("Build cancelled for build {buildId}", buildId);

        // Get building
        DbFortBuild build = await this.fortRepository.GetBuilding(buildId);

        if (build.BuildStatus is not FortBuildStatus.Construction)
        {
            throw new InvalidOperationException($"This building is not currently being upgraded.");
        }

        // Cancel build
        build.Level -= 1;
        build.BuildStartDate = DateTimeOffset.UnixEpoch;
        build.BuildEndDate = DateTimeOffset.UnixEpoch;

        if (build.Level == 0)
        {
            this.fortRepository.DeleteBuild(build);
        }

        return build;
    }

    public async Task EndUpgrade(long buildId)
    {
        this.logger.LogDebug("Build ended for build {buildId}", buildId);

        // Get building
        DbFortBuild build = await this.fortRepository.GetBuilding(buildId);

        if (build.BuildStatus is not FortBuildStatus.ConstructionComplete)
        {
            throw new InvalidOperationException($"This building has not completed construction.");
        }

        // Update values
        build.BuildStartDate = DateTimeOffset.UnixEpoch;
        build.BuildEndDate = DateTimeOffset.UnixEpoch;
    }

    public async Task<DbFortBuild> BuildStart(FortPlants fortPlantId, int positionX, int positionZ)
    {
        // Get build plans
        FortPlantDetail plantDetail = MasterAsset.FortPlant.Enumerable
            .Where(x => x.AssetGroup == fortPlantId)
            .OrderBy(x => x.Level)
            .First();

        DateTimeOffset startDate,
            endDate;

        if (plantDetail.Time == 0)
        {
            startDate = DateTimeOffset.UnixEpoch;
            endDate = DateTimeOffset.UnixEpoch;
        }
        else
        {
            startDate = DateTimeOffset.UtcNow;
            endDate = startDate.AddSeconds(plantDetail.Time); // TODO: Add 1/2 time reduction for first 30 days
        }

        DbFortBuild build =
            new()
            {
                DeviceAccountId = this.playerIdentityService.AccountId,
                PlantId = fortPlantId,
                Level = plantDetail.Level,
                PositionX = positionX,
                PositionZ = positionZ,
                BuildStartDate = startDate,
                BuildEndDate = endDate,
                IsNew = true,
                LastIncomeDate = DateTimeOffset.UnixEpoch
            };

        await Upgrade(plantDetail);
        await this.fortRepository.AddBuild(build);

        return build;
    }

    public async Task<DbFortBuild> LevelupStart(long buildId)
    {
        this.logger.LogDebug("Levelup started for build {buildId}", buildId);

        // Get building
        DbFortBuild build = await this.fortRepository.GetBuilding(buildId);

        FortPlantDetail currentBuilding = MasterAsset.FortPlant[build.FortPlantDetailId];
        if (currentBuilding.NextAssetGroup == 0)
        {
            this.logger.LogError(
                "Tried to level up build {@build} but it has no next level",
                build
            );
            throw new DragaliaException(
                ResultCode.FortPlantDetailNotFound,
                "No next level available for building"
            );
        }

        // Get level up plans (current level+1 to get plans of the next level)
        int buildPlantId = currentBuilding.NextAssetGroup;

        if (!MasterAsset.FortPlant.TryGetValue(buildPlantId, out FortPlantDetail? plantDetail))
        {
            // This is unlikely to happen, but best to keep the check just in case

            this.logger.LogError(
                "Failed to lookup build information for upgrade of build {@build} to level {level}!",
                build,
                build.Level + 1
            );

            throw new DragaliaException(
                ResultCode.FortPlantDetailNotFound,
                "Could not find next building level in data"
            );
        }

        await Upgrade(plantDetail);

        // Start level up
        DateTimeOffset startDate = DateTimeOffset.UtcNow;
        DateTimeOffset endDate = startDate.AddSeconds(plantDetail.Time);

        build.Level += 1;
        build.BuildStartDate = startDate;
        build.BuildEndDate = endDate;

        return build;
    }

    public async Task<DbFortBuild> Move(long buildId, int afterPositionX, int afterPositionZ)
    {
        this.logger.LogDebug(
            "Move performed for build {buildId} - New Position {x}/{z}",
            buildId,
            afterPositionX,
            afterPositionZ
        );

        // Get building
        DbFortBuild build = await this.fortRepository.GetBuilding(buildId);

        // Move building to requested coordinate
        build.PositionX = afterPositionX;
        build.PositionZ = afterPositionZ;

        return build;
    }

    public async Task GetFortPlantIdList(IEnumerable<int> fortPlantIdList)
    {
        await this.fortRepository.GetFortPlantIdList(fortPlantIdList);
    }

    private async Task Upgrade(FortPlantDetail plantDetail)
    {
        FortDetail fortDetail = await this.GetFortDetail();

        // Check Carpenter available
        if (fortDetail.working_carpenter_num >= fortDetail.carpenter_num)
        {
            throw new DragaliaException(
                ResultCode.FortBuildCarpenterBusy,
                $"All carpenters are currently busy"
            );
        }

        // Remove resources from player
        await this.userDataRepository.UpdateCoin(-plantDetail.Cost);
        await this.inventoryRepository.UpdateQuantity(plantDetail.CreateMaterialMap);
    }
}
