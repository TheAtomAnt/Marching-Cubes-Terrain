﻿using System;
using System.Collections.Generic;
using Eldemarkki.VoxelTerrain.Utilities;
using Eldemarkki.VoxelTerrain.Utilities.Intersection;
using Eldemarkki.VoxelTerrain.World;
using Eldemarkki.VoxelTerrain.World.Chunks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Eldemarkki.VoxelTerrain.VoxelData
{
    /// <summary>
    /// A store which handles getting and setting the voxel data for the world
    /// </summary>
    public class VoxelDataStore : MonoBehaviour
    {
        /// <summary>
        /// A dictionary containing the chunks. Key is the chunk's coordinate, and the value is the chunk's voxel data volume
        /// </summary>
        private Dictionary<int3, VoxelDataVolume> _chunks;

        /// <summary>
        /// A dictionary of all the ongoing voxel data generation jobs. Key is the chunk's coordinate, and the value is the ongoing job for that chunk
        /// </summary>
        private Dictionary<int3, JobHandleWithData<IVoxelDataGenerationJob>> _generationJobHandles;

        /// <summary>
        /// The world that "owns" this voxel data store
        /// </summary>
        public VoxelWorld VoxelWorld { get; set; }

        private void Awake()
        {
            _chunks = new Dictionary<int3, VoxelDataVolume>();
            _generationJobHandles = new Dictionary<int3, JobHandleWithData<IVoxelDataGenerationJob>>();
        }

        private void OnApplicationQuit()
        {
            if(_chunks == null) { return; }

            foreach (VoxelDataVolume chunk in _chunks.Values)
            {
                if (chunk.IsCreated)
                {
                    chunk.Dispose();
                }
            }
        }

        /// <summary>
        /// Tries to get the voxel data from <paramref name="worldPosition"/>. If the position is not loaded, false will be returned and <paramref name="voxelData"/> will be set to 0 (Note that 0 doesn't directly mean that the position is not loaded). If it is loaded, true will be returned and <paramref name="voxelData"/> will be set to the value.
        /// </summary>
        /// <param name="worldPosition">The world position to get the voxel data from</param>
        /// <param name="voxelData">The voxel data value at the world position</param>
        /// <returns>Does a voxel data point exist at that position</returns>
        public bool TryGetVoxelData(int3 worldPosition, out float voxelData)
        {
            int3 chunkCoordinate = VectorUtilities.WorldPositionToCoordinate(worldPosition, VoxelWorld.WorldSettings.ChunkSize);
            ApplyChunkChanges(chunkCoordinate);
            if (_chunks.TryGetValue(chunkCoordinate, out VoxelDataVolume chunk))
            {
                int3 voxelDataLocalPosition = worldPosition.Mod(VoxelWorld.WorldSettings.ChunkSize);
                return chunk.TryGetVoxelData(voxelDataLocalPosition.x, voxelDataLocalPosition.y, voxelDataLocalPosition.z, out voxelData);
            }
            else
            {
                voxelData = 0;
                return false;
            }
        }

        /// <summary>
        /// Tries to get the <see cref="VoxelDataVolume"/> for one chunk with a persistent allocator. If a chunk doesn't exist there, false will be returned and <paramref name="chunk"/> will be set to null. If a chunk exists there, true will be returned and <paramref name="chunk"/> will be set to the chunk.
        /// </summary>
        /// <param name="chunkCoordinate">The coordinate of the chunk whose voxel data should be gotten</param>
        /// <param name="chunk">The voxel data of a chunk at the coordinate</param>
        /// <returns>Does a chunk exists at that coordinate</returns>
        public bool TryGetVoxelDataChunk(int3 chunkCoordinate, out VoxelDataVolume chunk)
        {
            ApplyChunkChanges(chunkCoordinate);
            return _chunks.TryGetValue(chunkCoordinate, out chunk);
        }

        /// <summary>
        /// Gets the voxel data of a custom volume in the world with a persistent allocator
        /// </summary>
        /// <param name="bounds">The world-space volume to get the voxel data for</param>
        /// <returns>The voxel data volume inside the bounds</returns>
        public VoxelDataVolume GetVoxelDataCustom(BoundsInt bounds)
        {
            return GetVoxelDataCustom(bounds, Allocator.Persistent);
        }

        /// <summary>
        /// Gets the voxel data of a custom volume in the world
        /// </summary>
        /// <param name="worldSpaceQuery">The world-space volume to get the voxel data for</param>
        /// <param name="allocator">How the new voxel data volume should be allocated</param>
        /// <returns>The voxel data volume inside the bounds</returns>
        public VoxelDataVolume GetVoxelDataCustom(BoundsInt worldSpaceQuery, Allocator allocator)
        {
            VoxelDataVolume voxelDataVolume = new VoxelDataVolume(worldSpaceQuery.size, allocator);

            int3 chunkSize = VoxelWorld.WorldSettings.ChunkSize;

            int3 minChunkCoordinate = VectorUtilities.WorldPositionToCoordinate(worldSpaceQuery.min - Vector3Int.one, chunkSize);
            int3 maxChunkCoordinate = VectorUtilities.WorldPositionToCoordinate(worldSpaceQuery.max + Vector3Int.one, chunkSize);

            for (int chunkCoordinateX = minChunkCoordinate.x; chunkCoordinateX <= maxChunkCoordinate.x; chunkCoordinateX++)
            {
                for (int chunkCoordinateY = minChunkCoordinate.y; chunkCoordinateY <= maxChunkCoordinate.y; chunkCoordinateY++)
                {
                    for (int chunkCoordinateZ = minChunkCoordinate.z; chunkCoordinateZ <= maxChunkCoordinate.z; chunkCoordinateZ++)
                    {
                        int3 chunkCoordinate = new int3(chunkCoordinateX, chunkCoordinateY, chunkCoordinateZ);
                        if (!TryGetVoxelDataChunk(chunkCoordinate, out VoxelDataVolume voxelDataChunk))
                        {
                            continue;
                        }

                        int3 chunkBoundsSize = new int3(voxelDataChunk.Width - 1, voxelDataChunk.Height - 1, voxelDataChunk.Depth - 1);
                        int3 chunkWorldSpaceOrigin = chunkCoordinate * chunkSize;

                        BoundsInt chunkWorldSpaceBounds = new BoundsInt(chunkWorldSpaceOrigin.ToVectorInt(), chunkBoundsSize.ToVectorInt());

                        BoundsInt intersectionVolume = IntersectionUtilities.GetIntersectionVolume(worldSpaceQuery, chunkWorldSpaceBounds);
                        int3 intersectionVolumeMin = intersectionVolume.min.ToInt3();
                        int3 intersectionVolumeMax = intersectionVolume.max.ToInt3();

                        for (int voxelDataWorldPositionX = intersectionVolumeMin.x; voxelDataWorldPositionX < intersectionVolumeMax.x; voxelDataWorldPositionX++)
                        {
                            for (int voxelDataWorldPositionY = intersectionVolumeMin.y; voxelDataWorldPositionY < intersectionVolumeMax.y; voxelDataWorldPositionY++)
                            {
                                for (int voxelDataWorldPositionZ = intersectionVolumeMin.z; voxelDataWorldPositionZ < intersectionVolumeMax.z; voxelDataWorldPositionZ++)
                                {
                                    int3 voxelDataWorldPosition = new int3(voxelDataWorldPositionX, voxelDataWorldPositionY, voxelDataWorldPositionZ);
                                    int3 voxelDataLocalPosition = voxelDataWorldPosition - chunkWorldSpaceOrigin;

                                    if (voxelDataChunk.TryGetVoxelData(voxelDataLocalPosition, out float voxelData))
                                    {
                                        voxelDataVolume.SetVoxelData(voxelData, voxelDataWorldPosition - worldSpaceQuery.min.ToInt3());
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return voxelDataVolume;
        }

        /// <summary>
        /// Increases the voxel data at <paramref name="worldPosition"/> by <paramref name="increaseAmount"/>.
        /// </summary>
        /// <param name="worldPosition">The world position of the voxel data that should be increased</param>
        public void IncreaseVoxelData(int3 worldPosition, float increaseAmount)
        {
            IEnumerable<int3> affectedChunkCoordinates = GetChunkCoordinatesContainingPoint(worldPosition, VoxelWorld.WorldSettings.ChunkSize);

            foreach(int3 chunkCoordinate in affectedChunkCoordinates)
            {
                if (!_chunks.ContainsKey(chunkCoordinate)) { continue; }

                if (TryGetVoxelDataChunk(chunkCoordinate, out VoxelDataVolume voxelDataVolume))
                {
                    int3 localPos = (worldPosition - chunkCoordinate * VoxelWorld.WorldSettings.ChunkSize).Mod(VoxelWorld.WorldSettings.ChunkSize + 1);
                    voxelDataVolume.IncreaseVoxelData(increaseAmount, localPos);

                    if (VoxelWorld.ChunkStore.TryGetChunkAtCoordinate(chunkCoordinate, out ChunkProperties chunkProperties))
                    {
                        chunkProperties.HasChanges = true;
                    }
                }
            }
        }

        /// <summary>
        /// Sets the voxel data for a world position
        /// </summary>
        /// <param name="voxelData">The new voxel data</param>
        /// <param name="worldPosition">The world position of the voxel data</param>
        public void SetVoxelData(float voxelData, int3 worldPosition)
        {
            IEnumerable<int3> affectedChunkCoordinates = GetChunkCoordinatesContainingPoint(worldPosition, VoxelWorld.WorldSettings.ChunkSize);

            foreach (int3 chunkCoordinate in affectedChunkCoordinates)
            {
                if (!_chunks.ContainsKey(chunkCoordinate)) { continue; }

                if (TryGetVoxelDataChunk(chunkCoordinate, out VoxelDataVolume voxelDataVolume))
                {
                    int3 localPos = (worldPosition - chunkCoordinate * VoxelWorld.WorldSettings.ChunkSize).Mod(VoxelWorld.WorldSettings.ChunkSize + 1);
                    voxelDataVolume.SetVoxelData(voxelData, localPos.x, localPos.y, localPos.z);

                    if (VoxelWorld.ChunkStore.TryGetChunkAtCoordinate(chunkCoordinate, out ChunkProperties chunkProperties))
                    {
                        chunkProperties.HasChanges = true;
                    }
                }
            }
        }

        /// <summary>
        /// Sets a chunk's voxel data
        /// </summary>
        /// <param name="chunkVoxelData">The new voxel data</param>
        /// <param name="chunkCoordinate">The coordinate of the chunk whose voxel data should be set</param>
        public void SetVoxelDataChunk(VoxelDataVolume chunkVoxelData, int3 chunkCoordinate)
        {
            if (_chunks.TryGetValue(chunkCoordinate, out VoxelDataVolume voxelDataVolume))
            {
                voxelDataVolume.CopyFrom(chunkVoxelData);
            }
            else
            {
                _chunks.Add(chunkCoordinate, chunkVoxelData);
            }

            if (VoxelWorld.ChunkStore.TryGetChunkAtCoordinate(chunkCoordinate, out ChunkProperties chunkProperties))
            {
                chunkProperties.HasChanges = true;
            }
        }

        /// <summary>
        /// Sets the voxel data for a volume in the world
        /// </summary>
        /// <param name="voxelDataVolume">The new voxel data volume</param>
        /// <param name="originPosition">The world position of the origin where the voxel data should be set</param>
        public void SetVoxelDataCustom(VoxelDataVolume voxelDataVolume, int3 originPosition)
        {
            BoundsInt worldSpaceQuery = new BoundsInt(originPosition.ToVectorInt(), (voxelDataVolume.Size - new int3(1, 1, 1)).ToVectorInt());

            int3 chunkSize = VoxelWorld.WorldSettings.ChunkSize;

            int3 minChunkCoordinate = VectorUtilities.WorldPositionToCoordinate(worldSpaceQuery.min - Vector3Int.one, chunkSize);
            int3 maxChunkCoordinate = VectorUtilities.WorldPositionToCoordinate(worldSpaceQuery.max + Vector3Int.one, chunkSize);

            for (int chunkCoordinateX = minChunkCoordinate.x; chunkCoordinateX <= maxChunkCoordinate.x; chunkCoordinateX++)
            {
                for (int chunkCoordinateY = minChunkCoordinate.y; chunkCoordinateY <= maxChunkCoordinate.y; chunkCoordinateY++)
                {
                    for (int chunkCoordinateZ = minChunkCoordinate.z; chunkCoordinateZ <= maxChunkCoordinate.z; chunkCoordinateZ++)
                    {
                        int3 chunkCoordinate = new int3(chunkCoordinateX, chunkCoordinateY, chunkCoordinateZ);
                        if (!TryGetVoxelDataChunk(chunkCoordinate, out VoxelDataVolume voxelDataChunk))
                        {
                            continue;
                        }

                        int3 chunkBoundsSize = new int3(voxelDataChunk.Width - 1, voxelDataChunk.Height - 1, voxelDataChunk.Depth - 1);
                        int3 chunkWorldSpaceOrigin = chunkCoordinate * chunkSize;

                        BoundsInt chunkWorldSpaceBounds = new BoundsInt(chunkWorldSpaceOrigin.ToVectorInt(), chunkBoundsSize.ToVectorInt());

                        BoundsInt intersectionVolume = IntersectionUtilities.GetIntersectionVolume(worldSpaceQuery, chunkWorldSpaceBounds);
                        int3 intersectionVolumeMin = intersectionVolume.min.ToInt3();
                        int3 intersectionVolumeMax = intersectionVolume.max.ToInt3();

                        for (int voxelDataWorldPositionX = intersectionVolumeMin.x; voxelDataWorldPositionX <= intersectionVolumeMax.x; voxelDataWorldPositionX++)
                        {
                            for (int voxelDataWorldPositionY = intersectionVolumeMin.y; voxelDataWorldPositionY <= intersectionVolumeMax.y; voxelDataWorldPositionY++)
                            {
                                for (int voxelDataWorldPositionZ = intersectionVolumeMin.z; voxelDataWorldPositionZ <= intersectionVolumeMax.z; voxelDataWorldPositionZ++)
                                {
                                    int3 voxelDataWorldPosition = new int3(voxelDataWorldPositionX, voxelDataWorldPositionY, voxelDataWorldPositionZ);

                                    if (voxelDataVolume.TryGetVoxelData(voxelDataWorldPosition - worldSpaceQuery.min.ToInt3(), out float voxelData))
                                    {
                                        voxelDataChunk.SetVoxelData(voxelData, voxelDataWorldPosition - chunkWorldSpaceOrigin);
                                    }
                                }
                            }
                        }

                        if (VoxelWorld.ChunkStore.TryGetChunkAtCoordinate(chunkCoordinate, out ChunkProperties chunkProperties))
                        {
                            chunkProperties.HasChanges = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Increases the voxel data for a volume in the world
        /// </summary>
        /// <param name="worldSpaceQuery">The volume where the voxel datas should be increased</param>
        /// <param name="increaseFunction">The function that calculates how much a voxel data should be increased by. The first argument is the world space position of the voxel data, and the second argument is the current voxel data. The return value is how much the voxel data should be increased by.</param>
        public void IncreaseVoxelDataCustom(BoundsInt worldSpaceQuery, Func<int3, float, float> increaseFunction)
        {            
            int3 chunkSize = VoxelWorld.WorldSettings.ChunkSize;

            int3 minChunkCoordinate = VectorUtilities.WorldPositionToCoordinate(worldSpaceQuery.min - Vector3Int.one, chunkSize);
            int3 maxChunkCoordinate = VectorUtilities.WorldPositionToCoordinate(worldSpaceQuery.max + Vector3Int.one, chunkSize);

            for (int chunkCoordinateX = minChunkCoordinate.x; chunkCoordinateX <= maxChunkCoordinate.x; chunkCoordinateX++)
            {
                for (int chunkCoordinateY = minChunkCoordinate.y; chunkCoordinateY <= maxChunkCoordinate.y; chunkCoordinateY++)
                {
                    for (int chunkCoordinateZ = minChunkCoordinate.z; chunkCoordinateZ <= maxChunkCoordinate.z; chunkCoordinateZ++)
                    {
                        int3 chunkCoordinate = new int3(chunkCoordinateX, chunkCoordinateY, chunkCoordinateZ);
                        if (!TryGetVoxelDataChunk(chunkCoordinate, out VoxelDataVolume voxelDataChunk))
                        {
                            continue;
                        }

                        int3 chunkBoundsSize = new int3(voxelDataChunk.Width - 1, voxelDataChunk.Height - 1, voxelDataChunk.Depth - 1);
                        int3 chunkWorldSpaceOrigin = chunkCoordinate * chunkSize;

                        BoundsInt chunkWorldSpaceBounds = new BoundsInt(chunkWorldSpaceOrigin.ToVectorInt(), chunkBoundsSize.ToVectorInt());

                        BoundsInt intersectionVolume = IntersectionUtilities.GetIntersectionVolume(worldSpaceQuery, chunkWorldSpaceBounds);
                        int3 intersectionVolumeMin = intersectionVolume.min.ToInt3();
                        int3 intersectionVolumeMax = intersectionVolume.max.ToInt3();

                        for (int voxelDataWorldPositionX = intersectionVolumeMin.x; voxelDataWorldPositionX <= intersectionVolumeMax.x; voxelDataWorldPositionX++)
                        {
                            for (int voxelDataWorldPositionY = intersectionVolumeMin.y; voxelDataWorldPositionY <= intersectionVolumeMax.y; voxelDataWorldPositionY++)
                            {
                                for (int voxelDataWorldPositionZ = intersectionVolumeMin.z; voxelDataWorldPositionZ <= intersectionVolumeMax.z; voxelDataWorldPositionZ++)
                                {
                                    int3 voxelDataWorldPosition = new int3(voxelDataWorldPositionX, voxelDataWorldPositionY, voxelDataWorldPositionZ);

                                    int3 voxelDataLocalPosition = voxelDataWorldPosition - chunkWorldSpaceOrigin;
                                    int voxelDataIndex = voxelDataChunk.GetIndex(voxelDataLocalPosition);
                                    if (voxelDataChunk.TryGetVoxelData(voxelDataIndex, out float voxelData))
                                    {
                                        float increaseAmount = increaseFunction(voxelDataWorldPosition, voxelData);
                                        voxelDataChunk.IncreaseVoxelData(increaseAmount, voxelDataIndex);
                                    }
                                }
                            }
                        }

                        if (VoxelWorld.ChunkStore.TryGetChunkAtCoordinate(chunkCoordinate, out ChunkProperties chunkProperties))
                        {
                            chunkProperties.HasChanges = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Sets the job handle for a chunk coordinate
        /// </summary>
        /// <param name="generationJobHandle">The job handle with data</param>
        /// <param name="chunkCoordinate">The coordinate of the chunk to set the job handle for</param>
        public void SetVoxelDataJobHandle(JobHandleWithData<IVoxelDataGenerationJob> generationJobHandle, int3 chunkCoordinate)
        {
            if (!_generationJobHandles.ContainsKey(chunkCoordinate))
            {
                _generationJobHandles.Add(chunkCoordinate, generationJobHandle);
            }
        }

        /// <summary>
        /// If the chunk coordinate has an ongoing voxel data generation job, it will get completed and it's result will be applied to the chunk
        /// </summary>
        /// <param name="chunkCoordinate">The coordinate of the chunk to apply changes for</param>
        private void ApplyChunkChanges(int3 chunkCoordinate)
        {
            if (_generationJobHandles.TryGetValue(chunkCoordinate, out JobHandleWithData<IVoxelDataGenerationJob> jobHandle))
            {
                jobHandle.JobHandle.Complete();
                SetVoxelDataChunk(jobHandle.JobData.OutputVoxelData, chunkCoordinate);
                _generationJobHandles.Remove(chunkCoordinate);
            }
        }

        /// <summary>
        /// Unloads the voxel data of chunks from the coordinates
        /// </summary>
        /// <param name="coordinatesToUnload">The list of chunk coordinates to unload</param>
        public void UnloadCoordinates(IEnumerable<int3> coordinatesToUnload)
        {
            foreach (int3 coordinate in coordinatesToUnload)
            {
                if (_chunks.TryGetValue(coordinate, out VoxelDataVolume voxelDataVolume))
                {
                    voxelDataVolume.Dispose();
                    _chunks.Remove(coordinate);
                }
            }
        }

        /// <summary>
        /// Gets a collection of chunks that contain a world position. For a chunk to contain a position, the position has to be inside of the chunk or on the chunk's edge
        /// </summary>
        /// <param name="worldPosition">The world position to check</param>
        /// <param name="chunkSize">The size of the chunks</param>
        /// <returns>A collection of chunk coordinates that contain the world position</returns>
        public static IEnumerable<int3> GetChunkCoordinatesContainingPoint(int3 worldPosition, int3 chunkSize)
        {
            int3 localPosition = VectorUtilities.Mod(worldPosition, chunkSize);

            int chunkCheckCountX = localPosition.x == 0 ? 1 : 0;
            int chunkCheckCountY = localPosition.y == 0 ? 1 : 0;
            int chunkCheckCountZ = localPosition.z == 0 ? 1 : 0;

            int3 origin = VectorUtilities.WorldPositionToCoordinate(worldPosition, chunkSize);

            // The origin (worldPosition as a chunk coordinate) is always included
            yield return origin;

            // The first corner can be skipped, since it's (0, 0, 0) and would just return origin
            for (int i = 1; i < 8; i++)
            {
                var cornerOffset = LookupTables.CubeCorners[i];
                if (cornerOffset.x <= chunkCheckCountX && cornerOffset.y <= chunkCheckCountY && cornerOffset.z <= chunkCheckCountZ)
                {
                    yield return origin - cornerOffset;
                }
            }
        }
    }
}
