using System.Collections.Generic;
using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Unity.Rendering;

public class ClothSimEcsSystem : JobComponentSystem
{
    static Dictionary<Mesh, ClothBarSimEcs> s_MeshToBarSimLookup = new Dictionary<Mesh, ClothBarSimEcs>();
    NativeArray<JobHandle> jobHandles;
    EntityQuery simGroup;

    enum EntityHighwater
    {
	kInitial = 5
    };
    
    static public void AddSharedComponents(Entity entity, Mesh mesh, EntityManager dstManager)
    {
        // jiv fixme: perform initialization off the main thread.
        //   * need a two-stage initialization so we can allocate a ClothBarSimEcs without
        //   * using it this frame, maybe do a pass after the ClothSimVertexJob.
        if (!s_MeshToBarSimLookup.ContainsKey(mesh))
        {
            NativeArray<int> pins = new NativeArray<int>(mesh.vertices.Length, Allocator.Persistent);

	    if (mesh.normals == null)
	    {
		for (int i=0,n=pins.Length; i<n; ++i) {
		    if (mesh.vertices[i].y > 0.3f)
			pins[i] = 1;
		}
	    }
	    else
	    {
		for (int i=0,n=pins.Length; i<n; ++i) {
		    if (mesh.normals[i].y >= 0.9f && mesh.vertices[i].y > 0.3f)
			pins[i] = 1;
		}
	    }
	    
            HashSet<Vector2Int> barLookup = new HashSet<Vector2Int>();
            int[] triangles = mesh.triangles;
            for (int i=0, n=triangles.Length; i<n; i += 3)
            {
                for (int j=0; j<3; ++j)
                {
                    Vector2Int pair = new Vector2Int();
                    pair.x = triangles[i + j];
                    pair.y = triangles[i + (j + 1)%3];
                    if (pair.x > pair.y) {
                        int temp = pair.x;
                        pair.x = pair.y;
                        pair.y = temp;
                    }

                    if (barLookup.Contains(pair) == false &&
			// two pinned verts can't move, so don't simulate them
			pins[pair.x] + pins[pair.y] != 2)
		    {
                        barLookup.Add(pair);
		    }
                }
            }
            List<Vector2Int> barList = new List<Vector2Int>(barLookup);
            NativeArray<Vector2Int> bars = new NativeArray<Vector2Int>(barList.ToArray(), Allocator.Persistent);
            NativeArray<float> barLengths = new NativeArray<float>(barList.Count, Allocator.Persistent);

            for (int i=0,n=barList.Count; i<n; ++i) {
                Vector2Int pair = barList[i];
                Vector3 p1 = mesh.vertices[pair.x];
                Vector3 p2 = mesh.vertices[pair.y];
                barLengths[i] = (p2 - p1).magnitude;
            }

            ClothBarSimEcs barSimEcs = new ClothBarSimEcs();
            barSimEcs.pins = pins;
            barSimEcs.barLengths = barLengths;
            barSimEcs.bars = bars;

            s_MeshToBarSimLookup.Add(mesh, barSimEcs);
        }
        
        dstManager.AddSharedComponentData(entity, s_MeshToBarSimLookup[mesh]);
    }

    [BurstCompile]    
    struct ClothBarSimJob : IJob {
	[NativeDisableContainerSafetyRestriction]
        public NativeArray<float3> vertices;
        [ReadOnly]
        public NativeArray<Vector2Int> bars;
        [ReadOnly]
        public NativeArray<float> barLengths;
        [ReadOnly]
        public NativeArray<int> pins;

        public void Execute() {
            for (int i=0,n=bars.Length; i<n; ++i)
            {
                Vector2Int pair = bars[i];

                float3 p1 = vertices[pair.x];
                float3 p2 = vertices[pair.y];
                int pin1 = pins[pair.x];
                int pin2 = pins[pair.y];

                float length = math.length(p2 - p1);
                float extra = (length - barLengths[i]) * .5f;
                float3 dir = math.normalize(p2 - p1);

                if (pin1 == 0 && pin2 == 0) {
                    vertices[pair.x] += extra * dir;
                    vertices[pair.y] -= extra * dir;
                } else if (pin1 == 0 && pin2 == 1) {
                    vertices[pair.x] += extra * dir * 2f;
                } else if (pin1 == 1 && pin2 == 0) {
                    vertices[pair.y] -= extra * dir * 2f;
                }
            }
        }
    }

    [BurstCompile]
    struct ClothSimVertexJob0 : IJobParallelForBatch {
        [ReadOnly]
        public float4x4 localToWorld;
        [ReadOnly]
        public float4x4 worldToLocal;
        [ReadOnly]
        public float3 gravity;
        [ReadOnly]
        public NativeArray<int> pins;
	[NativeDisableContainerSafetyRestriction]
        public NativeArray<float3> currentVertexState;
	[NativeDisableContainerSafetyRestriction]
        public NativeArray<float3> oldVertexState;

        public void Execute(int startIndex, int count) {
            for (int i=startIndex,n=startIndex+count; i<n; ++i)
            {
                if (pins[i] != 0)
                    continue;

                float3 oldVert = oldVertexState[i];
                float3 vert = currentVertexState[i];

                float3 startPos = vert;
                oldVert -= gravity;
                vert += (vert - oldVert);
                oldVert = startPos;

                float3 worldPos = math.transform(localToWorld, vert);
                if (worldPos.y < 0f) {
                    Vector3 oldWorldPos = math.transform(localToWorld, oldVert);
                    oldWorldPos.y = (worldPos.y - oldWorldPos.y) * .5f;
                    worldPos.y = 0.0f;
                    vert = math.transform(worldToLocal, worldPos);
                    oldVert = math.transform(worldToLocal, oldWorldPos);
                }

                oldVertexState[i] = oldVert;
                currentVertexState[i] = vert;
            }
        }
    };

    protected override void OnDestroy()
    {
        foreach (ClothBarSimEcs barSimEcs in s_MeshToBarSimLookup.Values)
        {
            barSimEcs.bars.Dispose();
            barSimEcs.barLengths.Dispose();
            barSimEcs.pins.Dispose();
        }

	if (jobHandles.Length > 0)
	    jobHandles.Dispose();
    }

    protected override void OnCreate()
    {
        simGroup = GetEntityQuery(new EntityQueryDesc
        {
            All = new []
            {
                ComponentType.ReadOnly<ClothBarSimEcs>(),
                typeof(RenderMesh),
                ComponentType.ReadOnly<LocalToWorld>()
            }
        });

        RequireForUpdate(simGroup);

	PotentiallyResizeBecauseNewEntityHighwater((int)EntityHighwater.kInitial);
    }

    private void PotentiallyResizeBecauseNewEntityHighwater(int newHighwater)
    {
	if (newHighwater > jobHandles.Length)
	{
	    if (jobHandles.Length > 0)
		jobHandles.Dispose();

	    jobHandles = new NativeArray<JobHandle>(newHighwater, Allocator.Persistent);
	}
    }

    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        float4 worldGravity = new float4(-Vector3.up * Time.DeltaTime*Time.DeltaTime, 0.0f);
        JobHandle combinedJobHandle = new JobHandle();

        int entityCount = simGroup.CalculateEntityCount();
        if (entityCount > 0)
        {
	    PotentiallyResizeBecauseNewEntityHighwater(entityCount);

            Entities.WithoutBurst().ForEach((RenderMesh renderMesh,
                                             ref DynamicBuffer<VertexStateCurrentElement> currentVertexState) =>
            {
		renderMesh.mesh.SetVertices(currentVertexState.Reinterpret<Vector3>().AsNativeArray());
	    }).Run();

            Entities.WithoutBurst().ForEach((int entityInQueryIndex,
                                             RenderMesh renderMesh,
                                             ref DynamicBuffer<VertexStateCurrentElement> currentVertexState,
                                             ref DynamicBuffer<VertexStateOldElement> oldVertexState,
                                             in ClothBarSimEcs clothBarSimEcs,
                                             in LocalToWorld localToWorld) =>
            {
                // jiv fixme: there's a WorldToLocal component that doesn't seem to be added automatically
                float4x4 worldToLocal = math.inverse(localToWorld.Value);

                NativeArray<float3> vertices = currentVertexState.Reinterpret<float3>().AsNativeArray();
                int vLength = clothBarSimEcs.pins.Length;

                ClothBarSimJob clothBarSimJob = new ClothBarSimJob {
                    vertices = vertices,
                    bars = clothBarSimEcs.bars,
                    barLengths = clothBarSimEcs.barLengths,
                    pins = clothBarSimEcs.pins
                };

                JobHandle clothBarSimJobHandle = clothBarSimJob.Schedule(inputDeps);

                ClothSimVertexJob0 clothSimVertexJob0 = new ClothSimVertexJob0 {
                    pins = clothBarSimEcs.pins,
                    localToWorld = localToWorld.Value,
                    worldToLocal = worldToLocal,
                    gravity = math.mul(worldToLocal, worldGravity).xyz,
                    currentVertexState = vertices,
                    oldVertexState = oldVertexState.Reinterpret<float3>().AsNativeArray()
                };

                JobHandle simVertexJobHandle = clothSimVertexJob0.ScheduleBatch(vLength, vLength/SystemInfo.processorCount, clothBarSimJobHandle);
		jobHandles[entityInQueryIndex] = JobHandle.CombineDependencies(clothBarSimJobHandle, simVertexJobHandle);
            }).Run();

            combinedJobHandle = JobHandle.CombineDependencies(new NativeSlice<JobHandle>(jobHandles, 0, entityCount));
        }

        return combinedJobHandle;
    }
}
