// Magica Cloth 2.
// Copyright (c) 2023 MagicaSoft.
// https://magicasoft.jp
using System.Collections.Generic;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace MagicaCloth2
{
    public class TransformManager : IManager, IValid
    {
        // フラグ
        internal const byte Flag_Read = 0x01;
        internal const byte Flag_WorldRotWrite = 0x02; // ワールド回転のみ書き込み
        internal const byte Flag_LocalPosRotWrite = 0x04; // ローカル座標・回転書き込み
        internal const byte Flag_Restore = 0x08; // 復元する
        internal const byte Flag_Enable = 0x10; // 有効状態
        internal ExNativeArray<ExBitFlag8> flagArray;

        /// <summary>
        /// 初期localPosition
        /// </summary>
        internal ExNativeArray<float3> initLocalPositionArray;

        /// <summary>
        /// 初期localRotation
        /// </summary>
        internal ExNativeArray<quaternion> initLocalRotationArray;

        /// <summary>
        /// ワールド座標
        /// </summary>
        internal ExNativeArray<float3> positionArray;

        /// <summary>
        /// ワールド回転
        /// </summary>
        internal ExNativeArray<quaternion> rotationArray;

        /// <summary>
        /// ワールド逆回転
        /// </summary>
        internal ExNativeArray<quaternion> inverseRotationArray;

        /// <summary>
        /// ワールドスケール
        /// Transform.lossyScaleと等価
        /// </summary>
        internal ExNativeArray<float3> scaleArray;

        /// <summary>
        /// ローカル座標
        /// </summary>
        internal ExNativeArray<float3> localPositionArray;

        /// <summary>
        /// ローカル回転
        /// </summary>
        internal ExNativeArray<quaternion> localRotationArray;

        /// <summary>
        /// 接続チームID(0=なし)
        /// </summary>
        internal ExNativeArray<short> teamIdArray;

        /// <summary>
        /// 読み込み用トランスフォームアクセス配列
        /// 有効なTransformのみ格納し、データ配列との対応は下記のマッピングで管理する
        /// </summary>
        internal TransformAccessArray transformAccessArray;

        // DataChunk indices remain stable; reserved/removed slots have no TransformAccess entry.
        readonly List<int> dataToTransformIndex = new List<int>();
        NativeList<int> transformToDataIndex;


        internal int Count => flagArray?.Count ?? 0;

        //=========================================================================================
        /// <summary>
        /// 書き込み用トランスフォームのデータ参照インデックス
        /// つまり上記配列へのインデックス
        /// </summary>
        //internal ExNativeArray<short> writeIndexArray;

        /// <summary>
        /// 書き込み用トランスフォームアクセス配列
        /// </summary>
        //internal TransformAccessArray writeTransformAccessArray;

        bool isValid;

        //=========================================================================================
        public void Dispose()
        {
            isValid = false;

            flagArray?.Dispose();
            initLocalPositionArray?.Dispose();
            initLocalRotationArray?.Dispose();
            positionArray?.Dispose();
            rotationArray?.Dispose();
            inverseRotationArray?.Dispose();
            scaleArray?.Dispose();
            localPositionArray?.Dispose();
            localRotationArray?.Dispose();
            teamIdArray?.Dispose();
            //writeIndexArray?.Dispose();

            flagArray = null;
            initLocalPositionArray = null;
            initLocalRotationArray = null;
            positionArray = null;
            rotationArray = null;
            inverseRotationArray = null;
            scaleArray = null;
            localPositionArray = null;
            localRotationArray = null;
            teamIdArray = null;
            //writeIndexArray = null;

            if (transformAccessArray.isCreated)
                transformAccessArray.Dispose();
            if (transformToDataIndex.IsCreated)
                transformToDataIndex.Dispose();
            dataToTransformIndex.Clear();
            //if (writeTransformAccessArray.isCreated)
            //    writeTransformAccessArray.Dispose();
        }

        public void EnterdEditMode()
        {
            Dispose();
        }

        public void Initialize()
        {
            Dispose();

            const int capacity = 256;
            flagArray = new ExNativeArray<ExBitFlag8>(capacity);
            initLocalPositionArray = new ExNativeArray<float3>(capacity);
            initLocalRotationArray = new ExNativeArray<quaternion>(capacity);
            positionArray = new ExNativeArray<float3>(capacity);
            rotationArray = new ExNativeArray<quaternion>(capacity);
            inverseRotationArray = new ExNativeArray<quaternion>(capacity);
            scaleArray = new ExNativeArray<float3>(capacity);
            localPositionArray = new ExNativeArray<float3>(capacity);
            localRotationArray = new ExNativeArray<quaternion>(capacity);
            teamIdArray = new ExNativeArray<short>(capacity);

            transformAccessArray = new TransformAccessArray(capacity);
            transformToDataIndex = new NativeList<int>(capacity, Allocator.Persistent);

            isValid = true;
        }

        public bool IsValid()
        {
            return isValid;
        }

        //=========================================================================================
        /// <summary>
        /// VirtualMeshのTransformDataを追加する
        /// </summary>
        /// <param name="tdata"></param>
        /// <returns></returns>
        internal DataChunk AddTransform(VirtualMeshContainer cmesh, int teamId)
        {
            if (isValid == false)
                return default;

            Debug.Assert(cmesh != null);
            int cnt = cmesh.GetTransformCount();

            // データコピー追加
            var c = flagArray.AddRange(cmesh.shareVirtualMesh.transformData.flagArray);
            initLocalPositionArray.AddRange(cmesh.shareVirtualMesh.transformData.initLocalPositionArray);
            initLocalRotationArray.AddRange(cmesh.shareVirtualMesh.transformData.initLocalRotationArray);

            // 領域のみ追加
            positionArray.AddRange(cnt);
            rotationArray.AddRange(cnt);
            inverseRotationArray.AddRange(cnt);
            scaleArray.AddRange(cnt);
            localPositionArray.AddRange(cnt);
            localRotationArray.AddRange(cnt);

            // チームID
            teamIdArray.AddRange(cnt, (short)teamId);

            // 空きデータ領域はTransformAccessArrayに登録しない
            for (int i = 0; i < cnt; i++)
            {
                Transform t = cmesh.GetTransformFromIndex(i);
                SetTransformAccess(c.startIndex + i, t);
            }

            return c;
        }

        /// <summary>
        /// Transformの領域のみ追加する
        /// </summary>
        /// <param name="count"></param>
        /// <returns></returns>
        internal DataChunk AddTransform(int count, int teamId)
        {
            if (isValid == false)
                return default;

            // 領域のみ追加する
            var c = flagArray.AddRange(count, default);
            initLocalPositionArray.AddRange(count);
            initLocalRotationArray.AddRange(count);
            positionArray.AddRange(count);
            rotationArray.AddRange(count);
            inverseRotationArray.AddRange(count);
            scaleArray.AddRange(count);
            localPositionArray.AddRange(count);
            localRotationArray.AddRange(count);

            // チームID
            teamIdArray.AddRange(count, (short)teamId);

            // データ領域のみ予約し、実際のTransformはSetTransformで登録する
            EnsureTransformIndexCapacity(c.startIndex + c.dataLength);

            return c;
        }

        /// <summary>
        /// Transform１つを追加する
        /// </summary>
        /// <param name="t"></param>
        /// <param name="flag"></param>
        /// <returns></returns>
        internal DataChunk AddTransform(Transform t, ExBitFlag8 flag, int teamId)
        {
            if (isValid == false)
                return default;

            // データコピー追加
            var c = flagArray.Add(flag);
            initLocalPositionArray.Add(t.localPosition);
            initLocalRotationArray.Add(t.localRotation);
            positionArray.Add(t.position);
            rotationArray.Add(t.rotation);
            inverseRotationArray.Add(math.inverse(t.rotation));
            scaleArray.Add(t.lossyScale);
            localPositionArray.Add(t.localPosition);
            localRotationArray.Add(t.localRotation);

            // チームID
            teamIdArray.Add((short)teamId);

            SetTransformAccess(c.startIndex, t);

            return c;
        }

        /// <summary>
        /// Transform情報を書き換える
        /// </summary>
        /// <param name="t"></param>
        /// <param name="flag"></param>
        /// <param name="index"></param>
        internal void SetTransform(Transform t, ExBitFlag8 flag, int index, int teamId)
        {
            if (isValid == false)
                return;

            if (t != null)
            {
                // データ設定
                flagArray[index] = flag;
                initLocalPositionArray[index] = t.localPosition;
                initLocalRotationArray[index] = t.localRotation;
                positionArray[index] = t.position;
                rotationArray[index] = t.rotation;
                inverseRotationArray[index] = math.inverse(t.rotation);
                scaleArray[index] = t.lossyScale;
                localPositionArray[index] = t.localPosition;
                localRotationArray[index] = t.localRotation;
                teamIdArray[index] = (short)teamId;
                SetTransformAccess(index, t);
            }
            else
            {
                // データクリア（無効化）
                flagArray[index] = default;
                SetTransformAccess(index, null);
                teamIdArray[index] = 0;
            }
        }

        /// <summary>
        /// Transform情報をコピーする
        /// </summary>
        /// <param name="fromIndex"></param>
        /// <param name="toIndex"></param>
        internal void CopyTransform(int fromIndex, int toIndex)
        {
            if (isValid == false)
                return;

            flagArray[toIndex] = flagArray[fromIndex];
            initLocalPositionArray[toIndex] = initLocalPositionArray[fromIndex];
            initLocalRotationArray[toIndex] = initLocalRotationArray[fromIndex];
            positionArray[toIndex] = positionArray[fromIndex];
            rotationArray[toIndex] = rotationArray[fromIndex];
            inverseRotationArray[toIndex] = inverseRotationArray[fromIndex];
            scaleArray[toIndex] = scaleArray[fromIndex];
            localPositionArray[toIndex] = localPositionArray[fromIndex];
            localRotationArray[toIndex] = localRotationArray[fromIndex];
            int accessIndex = dataToTransformIndex[fromIndex];
            SetTransformAccess(toIndex, accessIndex >= 0 ? transformAccessArray[accessIndex] : null);
            teamIdArray[toIndex] = teamIdArray[fromIndex];
        }

        /// <summary>
        /// トランスフォームを削除する
        /// </summary>
        /// <param name="c"></param>
        internal void RemoveTransform(DataChunk c)
        {
            if (isValid == false)
                return;
            if (c.IsValid == false)
                return;

            flagArray.RemoveAndFill(c);
            initLocalPositionArray.Remove(c);
            initLocalRotationArray.Remove(c);
            positionArray.Remove(c);
            rotationArray.Remove(c);
            inverseRotationArray.Remove(c);
            scaleArray.Remove(c);
            localPositionArray.Remove(c);
            localRotationArray.Remove(c);
            teamIdArray.RemoveAndFill(c, 0);

            // トランスフォーム削除
            for (int i = 0; i < c.dataLength; i++)
            {
                int index = c.startIndex + i;
                SetTransformAccess(index, null);
            }
        }

        void EnsureTransformIndexCapacity(int length)
        {
            while (dataToTransformIndex.Count < length)
                dataToTransformIndex.Add(-1);
        }

        void SetTransformAccess(int dataIndex, Transform t)
        {
            EnsureTransformIndexCapacity(dataIndex + 1);
            int accessIndex = dataToTransformIndex[dataIndex];
            if (t != null)
            {
                if (accessIndex >= 0)
                    transformAccessArray[accessIndex] = t;
                else
                {
                    dataToTransformIndex[dataIndex] = transformAccessArray.length;
                    transformAccessArray.Add(t);
                    transformToDataIndex.Add(dataIndex);
                }
            }
            else if (accessIndex >= 0)
            {
                int movedDataIndex = transformToDataIndex[transformToDataIndex.Length - 1];
                transformAccessArray.RemoveAtSwapBack(accessIndex);
                transformToDataIndex.RemoveAtSwapBack(accessIndex);
                dataToTransformIndex[dataIndex] = -1;
                if (accessIndex < transformToDataIndex.Length)
                    dataToTransformIndex[movedDataIndex] = accessIndex;
            }
        }

        /// <summary>
        /// トランスフォームの有効状態を切り替える
        /// </summary>
        /// <param name="c"></param>
        /// <param name="sw">true=有効, false=無効</param>
        internal void EnableTransform(DataChunk c, bool sw)
        {
            if (isValid == false)
                return;
            if (c.IsValid == false)
                return;

            var job = new EnableTransformJob()
            {
                chunk = c,
                sw = sw,
                flagList = flagArray.GetNativeArray(),
            };
            job.Run();
        }

        [BurstCompile]
        struct EnableTransformJob : IJob
        {
            public DataChunk chunk;
            public bool sw;
            public NativeArray<ExBitFlag8> flagList;

            public void Execute()
            {
                for (int i = 0; i < chunk.dataLength; i++)
                {
                    int index = chunk.startIndex + i;

                    var flag = flagList[index];
                    if (flag.Value == 0)
                        continue;

                    flag.SetFlag(Flag_Enable, sw);
                    flagList[index] = flag;
                }
            }
        }

        /// <summary>
        /// トランスフォームの有効状態を切り替える
        /// </summary>
        /// <param name="index"></param>
        /// <param name="sw">true=有効, false=無効</param>
        internal void EnableTransform(int index, bool sw)
        {
            if (isValid == false)
                return;
            if (index < 0)
                return;

            var flag = flagArray[index];
            if (flag.Value == 0)
                return;
            flag.SetFlag(Flag_Enable, sw);
            flagArray[index] = flag;
        }

        internal DataChunk Expand(DataChunk c, int newLength)
        {
            if (isValid == false)
                return default;

            // 領域
            var nc = flagArray.ExpandAndFill(c, newLength);
            initLocalPositionArray.Expand(c, newLength);
            initLocalRotationArray.Expand(c, newLength);
            positionArray.Expand(c, newLength);
            rotationArray.Expand(c, newLength);
            inverseRotationArray.Expand(c, newLength);
            scaleArray.Expand(c, newLength);
            localPositionArray.Expand(c, newLength);
            localRotationArray.Expand(c, newLength);

            // チームID
            teamIdArray.ExpandAndFill(c, newLength);

            // Transform自体は移動せず、データ参照先だけ新しいチャンクへ移す
            if (c.startIndex != nc.startIndex)
            {
                EnsureTransformIndexCapacity(nc.startIndex + nc.dataLength);

                for (int i = 0; i < c.dataLength; i++)
                {
                    int oldIndex = c.startIndex + i;
                    int newIndex = nc.startIndex + i;
                    int accessIndex = dataToTransformIndex[oldIndex];
                    dataToTransformIndex[newIndex] = accessIndex;
                    dataToTransformIndex[oldIndex] = -1;
                    if (accessIndex >= 0)
                        transformToDataIndex[accessIndex] = newIndex;
                }
            }

            return nc;
        }

        //=========================================================================================
        /// <summary>
        /// Transformを初期姿勢で復元させるジョブを発行する
        /// </summary>
        /// <param name="count"></param>
        /// <param name="jobHandle"></param>
        /// <returns></returns>
        public JobHandle RestoreTransform(JobHandle jobHandle)
        {
            if (Count > 0)
            {
                //Debug.Log("RestoreTransform");
                var job = new RestoreTransformJob()
                {
                    transformToDataIndex = transformToDataIndex.AsArray(),
                    flagList = flagArray.GetNativeArray(),
                    localPositionArray = initLocalPositionArray.GetNativeArray(),
                    localRotationArray = initLocalRotationArray.GetNativeArray(),
                    teamIdArray = teamIdArray.GetNativeArray(),

                    teamDataArray = MagicaManager.Team.teamDataArray.GetNativeArray(),
                };
                jobHandle = job.Schedule(transformAccessArray, jobHandle);
            }

            return jobHandle;
        }

        [BurstCompile]
        struct RestoreTransformJob : IJobParallelForTransform
        {
            [Unity.Collections.ReadOnly]
            public NativeArray<int> transformToDataIndex;

            [Unity.Collections.ReadOnly]
            public NativeArray<ExBitFlag8> flagList;

            [Unity.Collections.ReadOnly]
            public NativeArray<float3> localPositionArray;
            [Unity.Collections.ReadOnly]
            public NativeArray<quaternion> localRotationArray;
            [Unity.Collections.ReadOnly]
            public NativeArray<short> teamIdArray;

            // team
            [Unity.Collections.ReadOnly]
            public NativeArray<TeamManager.TeamData> teamDataArray;


            public void Execute(int index, TransformAccess transform)
            {
                if (transform.isValid == false)
                    return;
                index = transformToDataIndex[index];
                var flag = flagList[index];
                if (flag.IsSet(Flag_Enable) == false)
                    return;
                if (flag.IsSet(Flag_Restore) == false)
                    return;

                // Keepカリング時はスキップする
                int teamId = teamIdArray[index];
                var tdata = teamDataArray[teamId];
                if (tdata.IsCullingInvisible && tdata.IsCullingKeep)
                    return;

                transform.localPosition = localPositionArray[index];
                transform.localRotation = localRotationArray[index];
            }
        }

        //=========================================================================================
        /// <summary>
        /// トランスフォームを読み込むジョブを発行する
        /// </summary>
        /// <param name="jobHandle"></param>
        /// <returns></returns>
        public JobHandle ReadTransform(JobHandle jobHandle)
        {
            if (Count > 0)
            {
                // todo:未来予測などがあると色々複雑化するところ

                var job = new ReadTransformJob()
                {
                    transformToDataIndex = transformToDataIndex.AsArray(),
                    flagList = flagArray.GetNativeArray(),
                    positionArray = positionArray.GetNativeArray(),
                    rotationArray = rotationArray.GetNativeArray(),
                    scaleList = scaleArray.GetNativeArray(),
                    localPositionArray = localPositionArray.GetNativeArray(),
                    localRotationArray = localRotationArray.GetNativeArray(),
                    inverseRotationArray = inverseRotationArray.GetNativeArray(),

                    teamIdArray = teamIdArray.GetNativeArray(),

                    teamDataArray = MagicaManager.Team.teamDataArray.GetNativeArray(),
                };
                jobHandle = job.ScheduleReadOnly(transformAccessArray, 8, jobHandle);
            }

            return jobHandle;
        }

        [BurstCompile]
        struct ReadTransformJob : IJobParallelForTransform
        {
            [Unity.Collections.ReadOnly]
            public NativeArray<int> transformToDataIndex;

            [Unity.Collections.ReadOnly]
            public NativeArray<ExBitFlag8> flagList;

            // Each access entry maps to a unique data slot, which can differ from the job index.
            [NativeDisableParallelForRestriction]
            [Unity.Collections.WriteOnly]
            public NativeArray<float3> positionArray;
            [NativeDisableParallelForRestriction]
            [Unity.Collections.WriteOnly]
            public NativeArray<quaternion> rotationArray;
            [NativeDisableParallelForRestriction]
            [Unity.Collections.WriteOnly]
            public NativeArray<float3> scaleList;
            [NativeDisableParallelForRestriction]
            [Unity.Collections.WriteOnly]
            public NativeArray<float3> localPositionArray;
            [NativeDisableParallelForRestriction]
            [Unity.Collections.WriteOnly]
            public NativeArray<quaternion> localRotationArray;
            [NativeDisableParallelForRestriction]
            [Unity.Collections.WriteOnly]
            public NativeArray<quaternion> inverseRotationArray;

            [Unity.Collections.ReadOnly]
            public NativeArray<short> teamIdArray;

            // team
            [Unity.Collections.ReadOnly]
            public NativeArray<TeamManager.TeamData> teamDataArray;

            public void Execute(int index, TransformAccess transform)
            {
                if (transform.isValid == false)
                    return;
                index = transformToDataIndex[index];
                var flag = flagList[index];
                if (flag.IsSet(Flag_Enable) == false)
                    return;
                if (flag.IsSet(Flag_Read) == false)
                    return;

                // カリング時は書き込まない
                int teamId = teamIdArray[index];
                var tdata = teamDataArray[teamId];
                if (tdata.IsCullingInvisible)
                    return;

                var pos = transform.position;
                var rot = transform.rotation;
                float4x4 LtoW = transform.localToWorldMatrix;

                positionArray[index] = pos;
                rotationArray[index] = rot;
                localPositionArray[index] = transform.localPosition;
                localRotationArray[index] = transform.localRotation;

                // マトリックスから正確なスケール値を算出する（これはTransform.lossyScaleと等価）
                var irot = math.inverse(rot);
                var m2 = math.mul(new float4x4(irot, float3.zero), LtoW);
                var scl = new float3(m2.c0.x, m2.c1.y, m2.c2.z);
                scaleList[index] = scl;

                // ワールド->ローカル変換用の逆クォータニオン
                inverseRotationArray[index] = math.inverse(rot);
            }
        }

        //=========================================================================================
        /// <summary>
        /// Transformを書き込むジョブを発行する
        /// </summary>
        /// <param name="count"></param>
        /// <param name="jobHandle"></param>
        /// <returns></returns>
        public JobHandle WriteTransform(JobHandle jobHandle)
        {
            var job = new WriteTransformJob()
            {
                transformToDataIndex = transformToDataIndex.AsArray(),
                flagList = flagArray.GetNativeArray(),
                worldPositions = positionArray.GetNativeArray(),
                worldRotations = rotationArray.GetNativeArray(),
                localPositions = localPositionArray.GetNativeArray(),
                localRotations = localRotationArray.GetNativeArray(),

                teamIdArray = teamIdArray.GetNativeArray(),

                teamDataArray = MagicaManager.Team.teamDataArray.GetNativeArray(),
            };
            jobHandle = job.Schedule(transformAccessArray, jobHandle);

            return jobHandle;
        }

        [BurstCompile]
        struct WriteTransformJob : IJobParallelForTransform
        {
            [Unity.Collections.ReadOnly]
            public NativeArray<int> transformToDataIndex;

            [Unity.Collections.ReadOnly]
            public NativeArray<ExBitFlag8> flagList;

            [Unity.Collections.ReadOnly]
            public NativeArray<float3> worldPositions;
            [Unity.Collections.ReadOnly]
            public NativeArray<quaternion> worldRotations;
            [Unity.Collections.ReadOnly]
            public NativeArray<float3> localPositions;
            [Unity.Collections.ReadOnly]
            public NativeArray<quaternion> localRotations;

            [Unity.Collections.ReadOnly]
            public NativeArray<short> teamIdArray;

            // team
            [Unity.Collections.ReadOnly]
            public NativeArray<TeamManager.TeamData> teamDataArray;

            public void Execute(int index, TransformAccess transform)
            {
                if (transform.isValid == false)
                    return;
                index = transformToDataIndex[index];
                var flag = flagList[index];
                if (flag.IsSet(Flag_Enable) == false)
                    return;

                // カリング時は書き込まない
                int teamId = teamIdArray[index];
                var tdata = teamDataArray[teamId];
                if (tdata.IsCullingInvisible)
                    return;

                // 書き込み停止中ならスキップ
                if (tdata.flag.IsSet(TeamManager.Flag_SkipWriting))
                    return;

                if (flag.IsSet(Flag_WorldRotWrite))
                {
                    // ワールド回転
                    transform.rotation = worldRotations[index];

                    // BoneSpringのみワールド座標を書き込む
                    if (tdata.IsSpring)
                        transform.position = worldPositions[index];
                }
                else if (flag.IsSet(Flag_LocalPosRotWrite))
                {
                    // ローカル座標・回転を書き込む
                    transform.localPosition = localPositions[index];
                    transform.localRotation = localRotations[index];
                }
            }
        }

        //=========================================================================================
        public void InformationLog(StringBuilder allsb)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"========== Transform Manager ==========");
            if (IsValid() == false)
            {
                sb.AppendLine($"Transform Manager. Invalid.");
            }
            else
            {
                int tcnt = transformAccessArray.isCreated ? transformAccessArray.length : 0;
                sb.AppendLine($"Transform Manager. Length:{tcnt}");
                sb.AppendLine($"  -flagArray:{flagArray.ToSummary()}");
                sb.AppendLine($"  -initLocalPositionArray:{initLocalPositionArray.ToSummary()}");
                sb.AppendLine($"  -initLocalRotationArray:{initLocalRotationArray.ToSummary()}");
                sb.AppendLine($"  -positionArray:{positionArray.ToSummary()}");
                sb.AppendLine($"  -rotationArray:{rotationArray.ToSummary()}");
                sb.AppendLine($"  -inverseRotationArray:{inverseRotationArray.ToSummary()}");
                sb.AppendLine($"  -scaleArray:{scaleArray.ToSummary()}");
                sb.AppendLine($"  -localPositionArray:{localPositionArray.ToSummary()}");
                sb.AppendLine($"  -localRotationArray:{localRotationArray.ToSummary()}");
                sb.AppendLine($"  -teamIdArray:{teamIdArray.ToSummary()}");

                if (transformAccessArray.isCreated)
                {
                    for (int i = 0; i < dataToTransformIndex.Count; i++)
                    {
                        int accessIndex = dataToTransformIndex[i];
                        var t = accessIndex >= 0 ? transformAccessArray[accessIndex] : null;
                        var flag = flagArray[i];
                        var teamId = teamIdArray[i];
                        sb.Append($"  [{i}] team:{teamId} (");
                        sb.Append(flag.IsSet(Flag_Enable) ? "E" : "");
                        sb.Append(flag.IsSet(Flag_Restore) ? "R" : "");
                        sb.Append(flag.IsSet(Flag_Read) ? "r" : "");
                        sb.Append(flag.IsSet(Flag_WorldRotWrite) ? "W" : "");
                        sb.Append(flag.IsSet(Flag_LocalPosRotWrite) ? "w" : "");
                        sb.AppendLine($") {t?.name ?? "(null)"}");
                    }
                }
            }
            sb.AppendLine();
            Debug.Log(sb.ToString());
            allsb.Append(sb);
        }
    }
}
