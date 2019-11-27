﻿using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace AntPheromones_ECS
{
    [UpdateAfter(typeof(DropPheromoneSystem))]
    public class DiminishPheromoneSystem : JobComponentSystem
    {
        private EntityQuery _mapQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            this._mapQuery = GetEntityQuery(ComponentType.ReadOnly<Map>());
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            inputDeps.Complete();
            
            var map = this._mapQuery.GetSingleton<Map>();
            
            Entity pheromoneRValues = 
                GetEntityQuery(ComponentType.ReadWrite<PheromoneColourRValueBuffer>()).GetSingletonEntity();
            var pheromoneColourRValues = 
                GetBufferFromEntity<PheromoneColourRValueBuffer>()[pheromoneRValues];
            
            return new Job
            {
                TrailDecayRate = map.TrailDecayRate,
                PheromoneColours = pheromoneColourRValues.AsNativeArray()
            }.Schedule(arrayLength: map.Width * map.Width,
                innerloopBatchCount: 256,
                inputDeps);  
        }
        
        [BurstCompile]
        private struct Job : IJobParallelFor
        {
            public NativeArray<PheromoneColourRValueBuffer> PheromoneColours;
            public float TrailDecayRate;
            
            public void Execute(int index)
            {
                this.PheromoneColours[index] *= this.TrailDecayRate;
            }
        }
    }
}