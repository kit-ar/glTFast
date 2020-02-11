﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Networking;
using UnityEngine.Profiling;
using Unity.Jobs;
using Unity.Collections;
using System.Runtime.InteropServices;

namespace GLTFast {

    using Schema;

    public class GLTFast {

        const uint GLB_MAGIC = 0x46546c67;

        const string ErrorUnsupportedColorFormat = "Unsupported Color format {0}";
        const string ErrorUnsupportedType = "Unsupported {0} type {1}";
        const string ErrorUnsupportedPrimitiveMode = "Primitive mode {0} is untested!";

        public static readonly HashSet<string> supportedExtensions = new HashSet<string> {
            "KHR_draco_mesh_compression",
            "KHR_materials_pbrSpecularGlossiness",
            "KHR_materials_unlit",
            "KHR_texture_transform",
            "KHR_mesh_quantization"
        };

        enum ChunkFormat : uint
        {
            JSON = 0x4e4f534a,
            BIN = 0x004e4942
        }

        byte[][] buffers;
        NativeArray<byte>[] nativeBuffers;

        GlbBinChunk[] binChunks;
        UnityEngine.Material[] materials;
        List<UnityEngine.Object> resources;

        PrimitiveCreateContextBase[] primitiveContexts;

        Primitive[] primitives;
        int[] meshPrimitiveIndex;

        IMaterialGenerator materialGenerator;

        /// TODO: Some of these class members maybe could be passed
        /// between loading routines. Turn them into parameters or at
        /// least dispose them once all ingredients are ready.

        /// Main glTF data structure
        Root gltfRoot;

        /// optional glTF-binary buffer
        /// https://github.com/KhronosGroup/glTF/tree/master/specification/2.0#binary-buffer
        GlbBinChunk? glbBinChunk;
        Texture2D[] images = null;
        List<ImageCreateContext> imageCreateContexts;

        bool loadingError = false;
        public bool LoadingError { get { return loadingError; } private set { this.loadingError = value; } }

        static string GetUriBase( string url ) {
            var uri = new Uri(url);
            return new Uri( uri, ".").AbsoluteUri;
        }

        public GLTFast(IMaterialGenerator matGenerator) {
            materialGenerator = matGenerator;
        }

        void ParseJsonAndLoadBuffers( string json, string baseUri ) {
            gltfRoot = ParseJson(json);

            if(!CheckExtensionSupport(gltfRoot)) {
                loadingError = true;
                return;
            }

            var bufferCount = gltfRoot.buffers.Length;
            if(bufferCount>0) {
                buffers = new byte[bufferCount][];
                nativeBuffers = new NativeArray<byte>[bufferCount];
                binChunks = new GlbBinChunk[bufferCount];
            }

            for( int i=0; i<bufferCount;i++) {
                var buffer = gltfRoot.buffers[i];
                if( !string.IsNullOrEmpty(buffer.uri) ) {
                    if(buffer.uri.StartsWith("data:")) {
                        buffers[i] = DecodeEmbedBuffer(buffer.uri);
                        if(buffers[i]==null) {
                            Debug.LogError("Error loading embed buffer!");
                            loadingError = true;
                        }
                    } else {
                        LoadBuffer( i, baseUri+buffer.uri );
                    }
                }
            }
        }

        Root ParseJson(string json) {
            // JsonUtility sometimes creates non-null default instances of objects-type members
            // even though there are none in the original JSON.
            // This work-around makes sure not existent JSON nodes will be null in the result.

            // Step one: main JSON parsing
            Profiler.BeginSample("JSON main");
            var root = JsonUtility.FromJson<Root>(json);
            Profiler.EndSample();

            /// Step two:
            /// detect, if a secondary null-check is necessary.
            Profiler.BeginSample("JSON extension check");
            bool check = false;
            if(root.materials!=null) {
                for (int i = 0; i < root.materials.Length; i++) {
                    var mat = root.materials[i];
                    check = mat.extensions!=null &&
                    (
                        mat.extensions.KHR_materials_pbrSpecularGlossiness!=null
                        || mat.extensions.KHR_materials_unlit!=null
                    );
                    if(check) break;
                }
            }
            Profiler.EndSample();

            /// Step three:
            /// If we have to make an explicit check, parse the JSON again with a
            /// different, minimal Root class, where class members are serialized to
            /// the type string. In case the string is null, there's no JSON node.
            /// Otherwise the string would be empty ("").
            if(check) {
                Profiler.BeginSample("JSON secondary");
                var fakeRoot = JsonUtility.FromJson<FakeSchema.Root>(json);

                for (int i = 0; i < root.materials.Length; i++)
                {
                    var mat = root.materials[i];
                    if(mat.extensions == null) continue;
                    Assert.AreEqual(mat.name,fakeRoot.materials[i].name);
                    var fake = fakeRoot.materials[i].extensions;
                    if(fake.KHR_materials_unlit==null) {
                        mat.extensions.KHR_materials_unlit = null;
                    }
                    if(fake.KHR_materials_pbrSpecularGlossiness==null) {
                        mat.extensions.KHR_materials_pbrSpecularGlossiness = null;
                    }
                }
                Profiler.EndSample();
            }
            return root;
        }

        /// <summary>
        /// Validates required and used glTF extensions and reports unsupported ones.
        /// </summary>
        /// <param name="gltfRoot"></param>
        /// <returns>False if a required extension is not supported. True otherwise.</returns>
        bool CheckExtensionSupport (Root gltfRoot) {
            if(gltfRoot.extensionsRequired!=null) {
                foreach(var ext in gltfRoot.extensionsRequired) {
                    var supported = supportedExtensions.Contains(ext);
                    if(!supported) {
                        Debug.LogErrorFormat("Required glTF extension {0} is not supported!",ext);
                        return false;
                    }
                }
            }
            if(gltfRoot.extensionsUsed!=null) {
                foreach(var ext in gltfRoot.extensionsUsed) {
                    var supported = supportedExtensions.Contains(ext);
                    if(!supported) {
                        Debug.LogWarningFormat("glTF extension {0} is not supported!",ext);
                    }
                }
            }
            return true;
        }

        public void LoadGltf( string json, string url ) {
            var baseUri = GetUriBase(url);
            ParseJsonAndLoadBuffers(json,baseUri);
            if(!loadingError) {
                LoadImages(baseUri);
            }
        }

        void LoadImages( string baseUri ) {

            if (gltfRoot.images != null) {
                images = new Texture2D[gltfRoot.images.Length];
                for (int i = 0; i < images.Length; i++) {
                    var img = gltfRoot.images[i];

                    if(!string.IsNullOrEmpty(img.uri) && img.uri.StartsWith("data:")) {
                        string mimeType;
                        var data = DecodeEmbedBuffer(img.uri,out mimeType);
                        if(data==null || !IsKnownImageMimeType(mimeType)) {
                            Debug.LogError("Loading embedded image failed");
                            continue;
                        }
                        var txt = CreateEmptyTexture(img,i);
                        txt.LoadImage(data);
                        images[i] = txt;
                    } else {
                        bool knownImageType = false;
                        if(string.IsNullOrEmpty(img.mimeType)) {
                            knownImageType = IsKnownImageFileExtension(img.uri);
                        } else {
                            knownImageType = IsKnownImageMimeType(img.mimeType);
                        }

                        if (knownImageType) {
                            if (img.bufferView < 0 && !string.IsNullOrEmpty(img.uri))
                            {
                                // Not Inside buffer
                                LoadTexture(i,baseUri+img.uri);
                            }
                        } else {
                            Debug.LogErrorFormat("Unknown image format (image {0};uri:{1})",i,img.uri);
                        }
                    }
                }
            }
        }

        public IEnumerator WaitForBufferDownloads() {
            if(downloads!=null) {
                foreach( var dl in downloads ) {
                    yield return dl.Value;
                    var www = dl.Value.webRequest;
                    if(www.isNetworkError || www.isHttpError) {
                        Debug.LogError(www.error);
                    }
                    else {
                        buffers[dl.Key] = www.downloadHandler.data;
                    }
                }
            }

            for( int i=0; i<buffers.Length; i++ ) {
                if(i==0 && glbBinChunk.HasValue) {
                    // Already assigned in LoadGlb
                    continue;
                }
                var b = buffers[i];
                binChunks[i] = new GlbBinChunk(0,(uint) b.Length);
            }
        }

        public IEnumerator WaitForTextureDownloads() {
            if(textureDownloads!=null) {
                foreach( var dl in textureDownloads ) {
                    yield return dl.Value;
                    var www = dl.Value.webRequest;
                    if(www.isNetworkError || www.isHttpError) {
                        Debug.LogError(www.error);
                    }
                    else {
                        images[dl.Key] = ( www.downloadHandler as  DownloadHandlerTexture ).texture;
                    }
                }
            }
        }

        public bool InstantiateGltf( Transform parent ) {
            CreateGameObjects( gltfRoot, parent );
            return !loadingError;
        }

        Dictionary<int,UnityWebRequestAsyncOperation> downloads;
        Dictionary<int,UnityWebRequestAsyncOperation> textureDownloads;

        void LoadBuffer( int index, string url ) {
            UnityWebRequest www = UnityWebRequest.Get(url);

            if(downloads==null) {
                downloads = new Dictionary<int, UnityWebRequestAsyncOperation>();
            }

            downloads[index] = www.SendWebRequest();
        }

        byte[] DecodeEmbedBuffer(string encodedBytes) {
            string tmp;
            return DecodeEmbedBuffer(encodedBytes,out tmp);
        }

        byte[] DecodeEmbedBuffer(string encodedBytes, out string mimeType) {
            mimeType = null;
            Debug.LogWarning("JSON embed buffers are slow! consider using glTF binary");
            var mediaTypeEnd = encodedBytes.IndexOf(';',5,Math.Min(encodedBytes.Length-5,1000) );
            if(mediaTypeEnd<0) return null;
            mimeType = encodedBytes.Substring(5,mediaTypeEnd-5);
            var tmp = encodedBytes.Substring(mediaTypeEnd+1,7);
            if(tmp!="base64,") return null;
            return System.Convert.FromBase64String(encodedBytes.Substring(mediaTypeEnd+8));
        }

        void LoadTexture( int index, string url ) {
            var www = UnityWebRequestTexture.GetTexture(url);

            if(textureDownloads==null) {
                textureDownloads = new Dictionary<int, UnityWebRequestAsyncOperation>();
            }

            textureDownloads[index] = www.SendWebRequest();
        }

        public bool LoadGlb( byte[] bytes, string url ) {
            uint magic = BitConverter.ToUInt32( bytes, 0 );

            if (magic != GLB_MAGIC) {
                loadingError = true;
                return false;
            }
    

            uint version = BitConverter.ToUInt32( bytes, 4 );
            //uint length = BitConverter.ToUInt32( bytes, 8 );

            //Debug.Log( string.Format("version: {0:X}; length: {1}", version, length ) );

            if (version != 2) {
                loadingError = true;
                return false;
            }

            int index = 12; // first chung header

            var baseUri = GetUriBase(url);

            while( index < bytes.Length ) {
                uint chLength = BitConverter.ToUInt32( bytes, index );
                index += 4;
                uint chType = BitConverter.ToUInt32( bytes, index );
                index += 4;

                //Debug.Log( string.Format("chunk: {0:X}; length: {1}", chType, chLength) );

                if (chType == (uint)ChunkFormat.BIN) {
                    //Debug.Log( string.Format("chunk: BIN; length: {0}", chLength) );
                    Assert.IsFalse(glbBinChunk.HasValue); // There can only be one binary chunk
                    glbBinChunk = new GlbBinChunk( index, chLength);
                }
                else if (chType == (uint)ChunkFormat.JSON) {
                    Assert.IsNull(gltfRoot);
                    string json = System.Text.Encoding.UTF8.GetString(bytes, index, (int)chLength );
                    //Debug.Log( string.Format("chunk: JSON; length: {0}", json ) );
                    ParseJsonAndLoadBuffers(json,baseUri);
                    if(loadingError) {
                        return false;
                    }
                }
 
                index += (int) chLength;
            }

            //Debug.Log(index);
            if(gltfRoot!=null) {
                //Debug.Log(gltf);
                if(glbBinChunk.HasValue) {
                    binChunks[0] = glbBinChunk.Value;
                    buffers[0] = bytes;
                }
                LoadImages(baseUri);
                return !loadingError;
            } else {
                Debug.LogError("Invalid JSON chunk");
                loadingError = true;
            }
            return false;
        }

        byte[] GetBuffer(int index) {
            return buffers[index];
        }

        NativeSlice<byte> GetBufferView(BufferView bufferView) {
            int bufferIndex = bufferView.buffer;
            if(!nativeBuffers[bufferIndex].IsCreated) {
                nativeBuffers[bufferIndex] = new NativeArray<byte>(GetBuffer(bufferIndex),Allocator.Persistent);
            }
            var chunk = binChunks[bufferIndex];
            return new NativeSlice<byte>(nativeBuffers[bufferIndex],chunk.start+bufferView.byteOffset,bufferView.byteLength);
        }

        public IEnumerator Prepare() {
            meshPrimitiveIndex = new int[gltfRoot.meshes.Length+1];

            resources = new List<UnityEngine.Object>();

            Profiler.BeginSample("CreateTexturesFromBuffers");
            if(gltfRoot.images!=null) {
                if(images==null) {
                    images = new Texture2D[gltfRoot.images.Length];
                } else {
                    Assert.AreEqual(images.Length,gltfRoot.images.Length);
                }
                imageCreateContexts = new List<ImageCreateContext>();
                CreateTexturesFromBuffers(gltfRoot.images,gltfRoot.bufferViews,imageCreateContexts);
            }
            Profiler.EndSample();
            yield return null;

            PreparePrimitives(gltfRoot);
            yield return null;

            if(imageCreateContexts!=null) {
                foreach(var jh in imageCreateContexts) {
                    while(!jh.jobHandle.IsCompleted) {
                        yield return null;
                    }
                    jh.jobHandle.Complete();
                    images[jh.imageIndex].LoadImage(jh.buffer);
                    jh.gcHandle.Free();
                }
                imageCreateContexts = null;
            }

            Profiler.BeginSample("GenerateMaterial");
            if(gltfRoot.materials!=null) {
                materials = new UnityEngine.Material[gltfRoot.materials.Length];
                for(int i=0;i<materials.Length;i++) {
                    materials[i] = materialGenerator.GenerateMaterial( gltfRoot.materials[i], gltfRoot.textures, images );
                }
            }
            Profiler.EndSample();
            yield return null;

            for(int i=0;i<primitiveContexts.Length;i++) {
                var primitiveContext = primitiveContexts[i];
                while(!primitiveContext.IsCompleted) {
                    yield return null;
                }
                var primitive = primitiveContext.CreatePrimitive();
                if(primitive.HasValue) {
                    primitives[primitiveContext.primtiveIndex] = primitive.Value;
                    resources.Add(primitive.Value.mesh);
                } else {
                    loadingError = true;
                }

                yield return null;
            }

            // Free temp resources
            primitiveContexts = null;
            DisposeBuffers();
        }

        void DisposeBuffers() {
            foreach (var nativeBuffer in nativeBuffers)
            {
                if(nativeBuffer.IsCreated) {
                    nativeBuffer.Dispose();
                }
            }
            nativeBuffers = null;
            buffers = null;
        }

        void CreateGameObjects( Root gltf, Transform parent ) {

            Profiler.BeginSample("CreateGameObjects");
            var nodes = new Transform[gltf.nodes.Length];
            var relations = new Dictionary<uint,uint>();

            for( uint nodeIndex = 0; nodeIndex < gltf.nodes.Length; nodeIndex++ ) {
                var node = gltf.nodes[nodeIndex];

                if( node.children==null && node.mesh<0 ) {
                    continue;
                }

                var goName = node.name;
                var go = new GameObject();
                nodes[nodeIndex] = go.transform;

                if(node.children!=null) {
                    foreach( var child in node.children ) {
                        relations[child] = nodeIndex;
                    }
                }

                if(node.matrix!=null) {
                    Matrix4x4 m = new Matrix4x4();
                    m.m00 = node.matrix[0];
                    m.m10 = node.matrix[1];
                    m.m20 = -node.matrix[2];
                    m.m30 = node.matrix[3];
                    m.m01 = node.matrix[4];
                    m.m11 = node.matrix[5];
                    m.m21 = -node.matrix[6];
                    m.m31 = node.matrix[7];
                    m.m02 = -node.matrix[8];
                    m.m12 = -node.matrix[9];
                    m.m22 = node.matrix[10];
                    m.m32 = node.matrix[11];
                    m.m03 = node.matrix[12];
                    m.m13 = node.matrix[13];
                    m.m23 = -node.matrix[14];
                    m.m33 = node.matrix[15];

                    if(m.ValidTRS()) {
                        go.transform.localPosition = new Vector3( m.m03, m.m13, m.m23 );
                        go.transform.localRotation = m.rotation;
                        go.transform.localScale = m.lossyScale;
                    } else {
                        Debug.LogErrorFormat("Invalid matrix on node {0}",nodeIndex);
                        Profiler.EndSample();
                        loadingError = true;
                        return;
                    }
                } else {
                    if(node.translation!=null) {
                        Assert.AreEqual( node.translation.Length, 3 );
                        go.transform.localPosition = new Vector3(
                            node.translation[0],
                            node.translation[1],
                            -node.translation[2]
                        );
                    }
                    if(node.rotation!=null) {
                        Assert.AreEqual( node.rotation.Length, 4 );
                        go.transform.localRotation = new Quaternion(
                            -node.rotation[0],
                            -node.rotation[1],
                            node.rotation[2],
                            node.rotation[3]
                        );
                    }
                    if(node.scale!=null) {
                        Assert.AreEqual( node.scale.Length, 3 );
                        go.transform.localScale = new Vector3(
                            node.scale[0],
                            node.scale[1],
                            node.scale[2]
                        );
                    }
                }

                if(node.mesh>=0) {
                    int end = meshPrimitiveIndex[node.mesh+1];
                    GameObject meshGo = null;
                    for( int i=meshPrimitiveIndex[node.mesh]; i<end; i++ ) {
                        var mesh = primitives[i].mesh;
                        var meshName = string.IsNullOrEmpty(mesh.name) ? null : mesh.name;
                        if(meshGo==null) {
                            meshGo = go;
                            goName = goName ?? meshName;
                        } else {
                            meshGo = new GameObject( meshName ?? "Primitive" );
                            meshGo.transform.SetParent(go.transform,false);
                        }
                        var mf = meshGo.AddComponent<MeshFilter>();
                        mf.mesh = mesh;
                        var mr = meshGo.AddComponent<MeshRenderer>();
                        
                        int materialIndex = primitives[i].materialIndex;
                        if(materials!=null && materialIndex>=0 && materialIndex<materials.Length ) {
                            mr.material = materials[primitives[i].materialIndex];
                        } else {
                            mr.material = materialGenerator.GetPbrMetallicRoughnessMaterial();
                        }
                    }
                }

                go.name = goName ?? "Node";
            }

            foreach( var rel in relations ) {
                if (nodes[rel.Key] != null) {
                    nodes[rel.Key].SetParent( nodes[rel.Value], false );
                }
            }

            foreach(var scene in gltf.scenes) {
                var go = new GameObject(scene.name ?? "Scene");
                go.transform.SetParent( parent, false);

                foreach(var nodeIndex in scene.nodes) {
                    if (nodes[nodeIndex] != null) {
                        nodes[nodeIndex].SetParent( go.transform, false );
                    }
                }
            }

            foreach( var bv in gltf.bufferViews ) {
                if(gltf.buffers[bv.buffer].uri == null) {
                    
                }
            }
            Profiler.EndSample();
        }

        unsafe void CreateTexturesFromBuffers( Schema.Image[] src_images, Schema.BufferView[] bufferViews, List<ImageCreateContext> contexts ) {
            for (int i = 0; i < images.Length; i++) {
                if(images[i]!=null) {
                    resources.Add(images[i]);
                }
                var img = src_images[i];
                bool knownImageType = false;
                if(string.IsNullOrEmpty(img.mimeType)) {
                    // Image is missing mime type
                    // try to determine type by file extension
                    knownImageType = IsKnownImageFileExtension(img.uri);
                } else {
                    knownImageType = IsKnownImageMimeType(img.mimeType);
                }

                if (knownImageType)
                {
                    if (img.bufferView >= 0)
                    {
                        var bufferView = bufferViews[img.bufferView];
                        var buffer = GetBuffer(bufferView.buffer);
                        var chunk = binChunks[bufferView.buffer];
                        var txt = CreateEmptyTexture(img,i);
                        var icc = new ImageCreateContext();
                        icc.imageIndex = i;
                        icc.buffer = new byte[bufferView.byteLength];
                        icc.gcHandle = GCHandle.Alloc(icc.buffer,GCHandleType.Pinned);
                        var job = new Jobs.MemCopyJob();
                        job.bufferSize = bufferView.byteLength;
                        fixed( void* src = &(buffer[bufferView.byteOffset + chunk.start]), dst = &(icc.buffer[0]) ) {
                            job.input = src;
                            job.result = dst;
                        }
                        icc.jobHandle = job.Schedule();
                        contexts.Add(icc);
                        
                        images[i] = txt;
                        resources.Add(txt);
                    }
                }
            }
        }

        Texture2D CreateEmptyTexture(Schema.Image img, int index) {
            var txt = new UnityEngine.Texture2D(4, 4);
            txt.name = string.IsNullOrEmpty(img.name) ? string.Format("image_{0}",index) : img.name;
            return txt;
        }

        public void Destroy() {
            if(materials!=null) {
                foreach( var material in materials ) {
                    UnityEngine.Object.Destroy(material);
                }
                materials = null;
            }

            if(resources!=null) {
                foreach( var resource in resources ) {
                    UnityEngine.Object.Destroy(resource);
                }
                resources = null;
            }
        }

        void PreparePrimitives( Root gltf ) {
            Profiler.BeginSample("PreparePrimitives");
            int totalPrimitives = 0;
            for( int meshIndex = 0; meshIndex<gltf.meshes.Length; meshIndex++ ) {
                var mesh = gltf.meshes[meshIndex];
                meshPrimitiveIndex[meshIndex] = totalPrimitives;
                totalPrimitives += mesh.primitives.Length;
            }
            meshPrimitiveIndex[gltf.meshes.Length] = totalPrimitives;

            primitives = new Primitive[totalPrimitives];
            primitiveContexts = new PrimitiveCreateContextBase[totalPrimitives];

            int i=0;
            for( int meshIndex = 0; meshIndex<gltf.meshes.Length; meshIndex++ ) {
                var mesh = gltf.meshes[meshIndex];
                foreach( var primitive in mesh.primitives ) {
                    
                    PrimitiveCreateContextBase context = null;

                    if( primitive.extensions!=null &&
                        primitive.extensions.KHR_draco_mesh_compression != null )
                    {
                        var c = new PrimitiveDracoCreateContext();
                        PreparePrimitiveDraco(gltf,mesh,primitive,ref c);
                        context = c;
                    } else {
                        var c = new PrimitiveCreateContext();
                        PreparePrimitive(gltf,mesh,primitive,ref c);
                        context = c;
                    }
                    context.primtiveIndex = i;
                    context.primitive = primitive;
                    primitiveContexts[i] = context;
                    i++;
                }
            }
            Profiler.EndSample();
        }

        unsafe void PreparePrimitive( Root gltf, Mesh mesh, MeshPrimitive primitive, ref PrimitiveCreateContext c ) {

            Profiler.BeginSample("PreparePrimitivePrepare");
            c.mesh = mesh;
            c.primitive = primitive;

            int jobHandlesCount = 2;
            if(primitive.attributes.NORMAL>=0) {
                jobHandlesCount++;
            }
            if(primitive.attributes.TANGENT>=0) {
                jobHandlesCount++;
            }
            if(primitive.attributes.TEXCOORD_0>=0) {
                jobHandlesCount++;
            }
            if(primitive.attributes.TEXCOORD_1>=0) {
                jobHandlesCount++;
            }
            if(primitive.attributes.COLOR_0>=0) {
                jobHandlesCount++;
            }
            NativeArray<JobHandle> jobHandles = new NativeArray<JobHandle>(jobHandlesCount, Allocator.TempJob);
            c.gcHandles = new GCHandle[jobHandlesCount];
            // from now on use it as a counter
            jobHandlesCount = 0;
            Profiler.EndSample();

            int vertexCount;
            {
                JobHandle? jh;
                vertexCount = GetVector3sJob(gltf,primitive.attributes.POSITION, out c.positions, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            }

            switch(primitive.mode) {
            case DrawMode.Triangles:
                c.topology = MeshTopology.Triangles;
                break;
            case DrawMode.Points:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.Points;
                break;
            case DrawMode.Lines:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.Lines;
                break;
            case DrawMode.LineStrip:
            case DrawMode.LineLoop:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.LineStrip;
                break;
            case DrawMode.TriangleStrip:
            case DrawMode.TriangleFan:
            default:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.Triangles;
                break;
            }

            if(primitive.indices < 0) {
                JobHandle? jh;
                CalculateIndicesJob(gltf,primitive, vertexCount, c.topology, out c.indices, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            } else {
                JobHandle? jh;
                GetIndicesJob(gltf,primitive.indices, out c.indices, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            }

            
            if(primitive.attributes.NORMAL>=0) {
                JobHandle? jh;
                GetVector3sJob(gltf,primitive.attributes.NORMAL, out c.normals, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            }

            if(primitive.attributes.TEXCOORD_0>=0) {
                JobHandle? jh;
                c.uvs0 = GetUvsJob(gltf,primitive.attributes.TEXCOORD_0, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            }
            if(primitive.attributes.TEXCOORD_1>=0) {
                JobHandle? jh;
                c.uvs1 = GetUvsJob(gltf,primitive.attributes.TEXCOORD_1, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            }

            if(primitive.attributes.TANGENT>=0) {
                JobHandle? jh;
                GetTangentsJob(gltf,primitive.attributes.TANGENT, out c.tangents, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            }

            if(primitive.attributes.COLOR_0>=0) {
                JobHandle? jh;
                GetColorsJob(gltf,primitive.attributes.COLOR_0, out c.colors32, out c.colors, out jh, out c.gcHandles[jobHandlesCount] );
                jobHandles[jobHandlesCount] = jh.Value;
                jobHandlesCount++;
            }

            c.jobHandle = JobHandle.CombineDependencies(jobHandles);
            jobHandles.Dispose();
        }

        void PreparePrimitiveDraco( Root gltf, Mesh mesh, MeshPrimitive primitive, ref PrimitiveDracoCreateContext c ) {
            var draco_ext = primitive.extensions.KHR_draco_mesh_compression;
            
            var bufferView = gltf.bufferViews[draco_ext.bufferView];
            var buffer = GetBufferView(bufferView);

            var job = new DracoMeshLoader.DracoJob();

            c.dracoResult = new NativeArray<int>(1,DracoMeshLoader.defaultAllocator);
            c.dracoPtr = new NativeArray<IntPtr>(1,DracoMeshLoader.defaultAllocator);

            job.data = buffer;
            job.result = c.dracoResult;
            job.outMesh = c.dracoPtr;

            c.jobHandle = job.Schedule();
        }

        void OnMeshesLoaded( Mesh mesh ) {
            Debug.Log("draco is ready");
        }

        unsafe Vector2[] GetUvsJob( Root gltf, int accessorIndex, out JobHandle? jobHandle, out GCHandle resultHandle ) {
            Profiler.BeginSample("PrepareUVs");
            
            var uvAccessor = gltf.accessors[accessorIndex];
            Assert.AreEqual( uvAccessor.typeEnum, GLTFAccessorAttributeType.VEC2 );
            #if DEBUG
            Assert.AreEqual( GetAccessorTye(uvAccessor.typeEnum), typeof(Vector2) );
            #endif

            var bufferView = gltf.bufferViews[uvAccessor.bufferView];
            var buffer = GetBuffer(bufferView.buffer);
            var chunk = binChunks[bufferView.buffer];
            var result = new Vector2[uvAccessor.count];
            resultHandle = GCHandle.Alloc(result, GCHandleType.Pinned);
            int start = uvAccessor.byteOffset + bufferView.byteOffset + chunk.start;

            switch( uvAccessor.componentType ) {
            case GLTFComponentType.Float:
                if (gltf.IsAccessorInterleaved(accessorIndex)) {
                    var jobUv = new Jobs.GetVector2sInterleavedJob();
                    jobUv.count = uvAccessor.count;
                    jobUv.byteStride = bufferView.byteStride;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        jobUv.input = (byte*)src;
                        jobUv.result = (Vector2*)dst;
                    }
                    jobHandle = jobUv.Schedule();
                } else {
                    var jobUv = new Jobs.GetUVsFloatJob();
                    jobUv.count = uvAccessor.count;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        jobUv.input = (float*)src;
                        jobUv.result = (Vector2*)dst;
                    }
                    jobHandle = jobUv.Schedule();
                }
                break;
            case GLTFComponentType.UnsignedByte:
                if (gltf.IsAccessorInterleaved(accessorIndex)) {
                    var jobUv = new Jobs.GetUVsUInt8InterleavedJob();
                    jobUv.count = uvAccessor.count;
                    jobUv.byteStride = bufferView.byteStride;
                    jobUv.normalize = uvAccessor.normalized;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        jobUv.input = (byte*) src;
                        jobUv.result = (Vector2*)dst;
                    }
                    jobHandle = jobUv.Schedule();
                } else {
                    var jobUv = new Jobs.GetUVsUInt8Job();
                    jobUv.count = uvAccessor.count;
                    jobUv.normalize = uvAccessor.normalized;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        jobUv.input = (byte*) src;
                        jobUv.result = (Vector2*)dst;
                    }
                    jobHandle = jobUv.Schedule();
                }
                break;
            case GLTFComponentType.UnsignedShort:
                if (gltf.IsAccessorInterleaved(accessorIndex)) {
                    var jobUv = new Jobs.GetUVsUInt16InterleavedJob();
                    jobUv.count = uvAccessor.count;
                    jobUv.byteStride = bufferView.byteStride;
                    jobUv.normalize = uvAccessor.normalized;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        jobUv.input = (byte*) src;
                        jobUv.result = (Vector2*)dst;
                    }
                    jobHandle = jobUv.Schedule();
                } else {
                    if(uvAccessor.normalized) {
                        var jobUv = new Jobs.GetUVsUInt16NormalizedJob();
                        jobUv.count = uvAccessor.count;
                        fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                            jobUv.input = (System.UInt16*) src;
                            jobUv.result = (Vector2*)dst;
                        }
                        jobHandle = jobUv.Schedule();
                    } else {
                        var jobUv = new Jobs.GetUVsUInt16Job();
                        jobUv.count = uvAccessor.count;
                        fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                            jobUv.input = (System.UInt16*) src;
                            jobUv.result = (Vector2*)dst;
                        }
                        jobHandle = jobUv.Schedule();
                    }
                }
                break;
            case GLTFComponentType.Short:
                var job = new Jobs.GetUVsInt16InterleavedJob();
                job.count = uvAccessor.count;
                job.byteStride = gltf.IsAccessorInterleaved(accessorIndex) ? bufferView.byteStride : 4;
                job.normalize = uvAccessor.normalized;
                fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                    job.input = (System.Int16*) src;
                    job.result = (Vector2*)dst;
                }
                jobHandle = job.Schedule();
                break;
            case GLTFComponentType.Byte:
                var jobInt8 = new Jobs.GetUVsInt8InterleavedJob();
                jobInt8.count = uvAccessor.count;
                jobInt8.byteStride = gltf.IsAccessorInterleaved(accessorIndex) ? bufferView.byteStride : 2;
                jobInt8.normalize = uvAccessor.normalized;
                fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                    jobInt8.input = (sbyte*) src;
                    jobInt8.result = (Vector2*)dst;
                }
                jobHandle = jobInt8.Schedule();
                break;
            default:
                jobHandle = null;
                Debug.LogErrorFormat( ErrorUnsupportedType, "UV", uvAccessor.componentType);
                break;
            }
            Profiler.EndSample();
            return result;
        }

        unsafe void CalculateIndicesJob(Root gltf, MeshPrimitive primitive, int vertexCount, MeshTopology topology, out int[] indices, out JobHandle? jobHandle, out GCHandle resultHandle ) {
            Profiler.BeginSample("CalculateIndicesJob");
            // No indices: calculate them
            bool lineLoop = primitive.mode == DrawMode.LineLoop;
            // extra index (first vertex again) for closing line loop
            indices = new int[vertexCount+(lineLoop?1:0)];
            resultHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            if(topology == MeshTopology.Triangles) {
                var job8 = new Jobs.CreateIndicesFlippedJob();
                job8.count = indices.Length;
                fixed( void* dst = &(indices[0]) ) {
                    job8.result = (int*)dst;
                }
                jobHandle = job8.Schedule();
            } else {
                var job8 = new Jobs.CreateIndicesJob();
                job8.count = indices.Length;
                job8.lineLoop = lineLoop;
                fixed( void* dst = &(indices[0]) ) {
                    job8.result = (int*)dst;
                }
                jobHandle = job8.Schedule();
            }
            Profiler.EndSample();
        }

        unsafe void GetIndicesJob(Root gltf, int accessorIndex, out int[] indices, out JobHandle? jobHandle, out GCHandle resultHandle ) {
            Profiler.BeginSample("PrepareIndicesJob");
            // index
            var accessor = gltf.accessors[accessorIndex];
            var bufferView = gltf.bufferViews[accessor.bufferView];
            int bufferIndex = bufferView.buffer;
            var buffer = GetBuffer(bufferIndex);

            indices = new int[accessor.count];
            resultHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);

            var chunk = binChunks[bufferIndex];
            Assert.AreEqual(accessor.typeEnum, GLTFAccessorAttributeType.SCALAR);
            //Assert.AreEqual(accessor.count * GetLength(accessor.typeEnum) * 4 , (int) chunk.length);
            var start = accessor.byteOffset + bufferView.byteOffset + chunk.start;

            switch( accessor.componentType ) {
            case GLTFComponentType.UnsignedByte:
                var job8 = new Jobs.GetIndicesUInt8Job();
                job8.count = accessor.count;
                fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                    job8.input = (byte*)src;
                    job8.result = (int*)dst;
                }
                jobHandle = job8.Schedule();
                break;
            case GLTFComponentType.UnsignedShort:
                var job16 = new Jobs.GetIndicesUInt16Job();
                job16.count = accessor.count;
                fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                    job16.input = (System.UInt16*) src;
                    job16.result = (int*) dst;
                }
                jobHandle = job16.Schedule();
                break;
            case GLTFComponentType.UnsignedInt:
                var job32 = new Jobs.GetIndicesUInt32Job();
                job32.count = accessor.count;
                fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                    job32.input = (System.UInt32*) src;
                    job32.result = (int*) dst;
                }
                jobHandle = job32.Schedule();
                break;
            default:
                Debug.LogErrorFormat( "Invalid index format {0}", accessor.componentType );
                jobHandle = null;
                break;
            }
            Profiler.EndSample();
        }

        unsafe int GetVector3sJob(Root gltf, int accessorIndex, out Vector3[] result, out JobHandle? jobHandle, out GCHandle resultHandle ) {
            Profiler.BeginSample("GetVector3sJob");
            Assert.IsTrue(accessorIndex>=0);
            #if DEBUG
            Assert.AreEqual( GetAccessorTye(gltf.accessors[accessorIndex].typeEnum), typeof(Vector3) );
            #endif

            var accessor = gltf.accessors[accessorIndex];
            var bufferView = gltf.bufferViews[accessor.bufferView];
            var buffer = GetBuffer(bufferView.buffer);
            var chunk = binChunks[bufferView.buffer];
            int count = accessor.count;
            result = new Vector3[count];
            resultHandle = GCHandle.Alloc(result, GCHandleType.Pinned);
            var start = accessor.byteOffset + bufferView.byteOffset + chunk.start;
            if (gltf.IsAccessorInterleaved(accessorIndex)) {
                if(accessor.componentType == GLTFComponentType.Float) {
                    var job = new Jobs.GetVector3sInterleavedJob();
                    job.count = count;
                    job.byteStride = bufferView.byteStride;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.input = (byte*)src;
                        job.result = (Vector3*)dst;
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedShort) {
                    var job = new Jobs.GetUInt16PositionsInterleavedJob();
                    job.count = count;
                    job.byteStride = bufferView.byteStride;
                    job.normalize = accessor.normalized;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.input = (byte*)src;
                        job.result = (Vector3*)dst;
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.Short) {
                    // TODO: test. did not have test files
                    var job = new Jobs.GetVector3FromInt16InterleavedJob();
                    job.count = count;
                    job.byteStride = bufferView.byteStride;
                    job.normalize = accessor.normalized;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.input = (byte*)src;
                        job.result = (Vector3*)dst;
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.Byte) {
                    // TODO: test positions. did not have test files
                    var job = new Jobs.GetVector3FromSByteInterleavedJob();
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.Setup(count,bufferView.byteStride,(sbyte*)src,(Vector3*)dst,accessor.normalized);
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedByte) {
                    // TODO: test. did not have test files
                    var job = new Jobs.GetVector3FromByteInterleavedJob();
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.Setup(count,bufferView.byteStride,(byte*)src,(Vector3*)dst,accessor.normalized);
                    }
                    jobHandle = job.Schedule();
                } else {
                    Debug.LogError("Unknown componentType");
                    jobHandle = null;
                }
            } else {
                if(accessor.componentType == GLTFComponentType.Float) {
                    var job = new Jobs.GetVector3sJob();
                    job.count = accessor.count;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.input = (float*)src;
                        job.result = (float*)dst;
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedShort) {
                    var job = new Jobs.GetUInt16PositionsJob();
                    job.count = accessor.count;
                    job.normalize = accessor.normalized;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.input = (System.UInt16*)src;
                        job.result = (Vector3*)dst;
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.Short) {
                    // TODO: test. did not have test files
                    // TODO: is a non-interleaved variant faster?
                    var job = new Jobs.GetVector3FromInt16InterleavedJob();
                    job.count = count;
                    job.byteStride = 6; // 2 bytes * 3
                    job.normalize = accessor.normalized;
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.input = (byte*)src;
                        job.result = (Vector3*)dst;
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.Byte) {
                    // TODO: test. did not have test files
                    // TODO: is a non-interleaved variant faster?
                    var job = new Jobs.GetVector3FromSByteInterleavedJob();
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.Setup(count,3,(sbyte*)src,(Vector3*)dst,accessor.normalized);
                    }
                    jobHandle = job.Schedule();
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedByte) {
                    // TODO: test. did not have test files
                    // TODO: is a non-interleaved variant faster?
                    var job = new Jobs.GetVector3FromByteInterleavedJob();
                    fixed( void* src = &(buffer[start]), dst = &(result[0]) ) {
                        job.Setup(count,3,(byte*)src,(Vector3*)dst,accessor.normalized);
                    }
                    jobHandle = job.Schedule();
                } else {
                    Debug.LogError("Unknown componentType");
                    jobHandle = null;
                }
            }
            Profiler.EndSample();
            return count;
        }

        unsafe void GetTangentsJob(Root gltf, int accessorIndex, out Vector4[] tangents, out JobHandle? jobHandle, out GCHandle resultHandle ) {
            Profiler.BeginSample("PrepareTangents");
            #if DEBUG
            Assert.AreEqual( GetAccessorTye(gltf.accessors[accessorIndex].typeEnum), typeof(Vector4) );
            #endif
            var accessor = gltf.accessors[accessorIndex];
            var bufferView = gltf.bufferViews[accessor.bufferView];
            var buffer = GetBuffer(bufferView.buffer);
            var chunk = binChunks[bufferView.buffer];
            tangents = new Vector4[accessor.count];
            resultHandle = GCHandle.Alloc(tangents, GCHandleType.Pinned);
            var start = accessor.byteOffset + bufferView.byteOffset + chunk.start;
            var interleaved = gltf.IsAccessorInterleaved((int)accessorIndex);
            switch(accessor.componentType) {
                case GLTFComponentType.Float:
                    if(interleaved) {
                        var jobTangentI = new Jobs.GetVector4sInterleavedJob();
                        jobTangentI.count = accessor.count;
                        jobTangentI.byteStride = bufferView.byteStride;
                        fixed( void* src = &(buffer[start]), dst = &(tangents[0]) ) {
                            jobTangentI.input = (byte*)src;
                            jobTangentI.result = (Vector4*)dst;
                        }
                        jobHandle = jobTangentI.Schedule();
                    } else {
                        var jobTangentFloat = new Jobs.GetVector4sJob();
                        jobTangentFloat.count = accessor.count;
                        fixed( void* src = &(buffer[start]), dst = &(tangents[0]) ) {
                            jobTangentFloat.input = (float*)src;
                            jobTangentFloat.result = (float*)dst;
                        }
                        jobHandle = jobTangentFloat.Schedule();
                    }
                    break;
                case GLTFComponentType.Short:
                    var jobTangent = new Jobs.GetVector4sInt16NormalizedInterleavedJob();
                    jobTangent.count = accessor.count;
                    jobTangent.byteStride = interleaved ? bufferView.byteStride : 8;
                    Assert.IsTrue(accessor.normalized);
                    fixed( void* src = &(buffer[start]), dst = &(tangents[0]) ) {
                        jobTangent.input = (System.Int16*)src;
                        jobTangent.result = (Vector4*)dst;
                    }
                    jobHandle = jobTangent.Schedule();
                    break;
                case GLTFComponentType.Byte:
                    var jobTangentByte = new Jobs.GetVector4sInt8NormalizedInterleavedJob();
                    jobTangentByte.count = accessor.count;
                    jobTangentByte.byteStride = interleaved ? bufferView.byteStride : 4;
                    Assert.IsTrue(accessor.normalized);
                    fixed( void* src = &(buffer[start]), dst = &(tangents[0]) ) {
                        jobTangentByte.input = (sbyte*)src;
                        jobTangentByte.result = (Vector4*)dst;
                    }
                    jobHandle = jobTangentByte.Schedule();
                    break;
                default:
                    Debug.LogErrorFormat( ErrorUnsupportedType, "Tangent", accessor.componentType);
                    jobHandle = null;
                    break;
            }
            Profiler.EndSample();
        }

        unsafe void GetColorsJob( Root gltf, int accessorIndex, out Color32[] colors32, out Color[] colors, out JobHandle? jobHandle, out GCHandle resultHandle ) {
            Profiler.BeginSample("PrepareColors");
            var colorAccessor = gltf.accessors[accessorIndex];
            var bufferView = gltf.bufferViews[colorAccessor.bufferView];
            var buffer = GetBuffer(bufferView.buffer);
            var chunk = binChunks[bufferView.buffer];
            var interleaved = gltf.IsAccessorInterleaved( accessorIndex );
            int start = colorAccessor.byteOffset + bufferView.byteOffset + chunk.start;

            if(colorAccessor.componentType == GLTFComponentType.UnsignedByte ) {
                colors32 = new Color32[colorAccessor.count];
                resultHandle = GCHandle.Alloc(colors32,GCHandleType.Pinned);
                colors = null;
            } else {
                colors = new Color[colorAccessor.count];
                resultHandle = GCHandle.Alloc(colors,GCHandleType.Pinned);
                colors32 = null;
            }
            jobHandle = null;

            if (colorAccessor.typeEnum == GLTFAccessorAttributeType.VEC3)
            {
                switch (colorAccessor.componentType)
                {
                    case GLTFComponentType.Float:
                        if(interleaved) {
                            // TODO
                            Debug.LogError("Not jobified yet!");
                        } else {
                            var job = new Jobs.GetColorsVec3FloatJob();
                            job.count = colorAccessor.count;
                            fixed( void* src = &(buffer[start]), dst = &(colors[0]) ) {
                                job.input = (float*) src;
                                job.result = (Color*)dst;
                            }
                            jobHandle = job.Schedule();
                        }
                        break;
                    case GLTFComponentType.UnsignedByte:
                        if(interleaved) {
                            // TODO
                            Debug.LogError("Not jobified yet!");
                        } else {
                            var job = new Jobs.GetColorsVec3UInt8Job();
                            job.count = colorAccessor.count;
                            fixed( void* src = &(buffer[start]), dst = &(colors32[0]) ) {
                                job.input = (byte*) src;
                                job.result = (Color32*)dst;
                            }
                            jobHandle = job.Schedule();
                        }
                        break;
                    case GLTFComponentType.UnsignedShort:
                        if(interleaved) {
                            // TODO
                            Debug.LogError("Not jobified yet!");
                        } else {
                            var job = new Jobs.GetColorsVec3UInt16Job();
                            job.count = colorAccessor.count;
                            fixed( void* src = &(buffer[start]), dst = &(colors[0]) ) {
                                job.input = (System.UInt16*) src;
                                job.result = (Color*)dst;
                            }
                            jobHandle = job.Schedule();
                        }
                        break;
                    default:
                        Debug.LogErrorFormat(ErrorUnsupportedColorFormat, colorAccessor.componentType);
                        break;
                }
            }
            else if (colorAccessor.typeEnum == GLTFAccessorAttributeType.VEC4)
            {
                switch (colorAccessor.componentType)
                {
                    case GLTFComponentType.Float:
                        if(interleaved) {
                            // TODO
                            Debug.LogError("Not jobified yet!");
                        } else {
                            var job = new Jobs.MemCopyJob();
                            job.bufferSize = colorAccessor.count*16;
                            fixed( void* src = &(buffer[start]), dst = &(colors[0]) ) {
                                job.input = src;
                                job.result = dst;
                            }
                            jobHandle = job.Schedule();
                        }
                        break;
                    case GLTFComponentType.UnsignedByte:
                        if(interleaved) {
                            // TODO
                            Debug.LogError("Not jobified yet!");
                        } else {
                            var job = new Jobs.MemCopyJob();
                            job.bufferSize = colorAccessor.count*4;
                            fixed( void* src = &(buffer[start]), dst = &(colors32[0]) ) {
                                job.input = src;
                                job.result = dst;
                            }
                            jobHandle = job.Schedule();
                        }
                        break;
                    case GLTFComponentType.UnsignedShort:
                        if(interleaved) {
                            // TODO
                            Debug.LogError("Not jobified yet!");
                        } else {
                            var job = new Jobs.GetColorsVec4UInt16Job();
                            job.count = colorAccessor.count;
                            fixed( void* src = &(buffer[start]), dst = &(colors[0]) ) {
                                job.input = (System.UInt16*) src;
                                job.result = (Color*)dst;
                            }
                            jobHandle = job.Schedule();
                        }
                        break;
                    default:
                        Debug.LogErrorFormat(ErrorUnsupportedColorFormat, colorAccessor.componentType);
                        break;
                }
            } else {
                Debug.LogErrorFormat( ErrorUnsupportedType, "color accessor", colorAccessor.typeEnum);
            }
            Profiler.EndSample();
        }

        bool IsKnownImageMimeType(string mimeType) {
            return mimeType == "image/jpeg"
            || mimeType == "image/png"
            // || mimeType == "image/ktx"
            ;
        }
        
        bool IsKnownImageFileExtension(string path) {
            return path.EndsWith(".png",StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg",StringComparison.OrdinalIgnoreCase)
                // || path.EndsWith(".ktx",StringComparison.OrdinalIgnoreCase)
                // || path.EndsWith(".ktx2",StringComparison.OrdinalIgnoreCase)
                ;
        }

#if DEBUG
        static Type GetAccessorTye( GLTFAccessorAttributeType accessorAttributeType ) {
            switch (accessorAttributeType)
            {
                case GLTFAccessorAttributeType.SCALAR:
                    return typeof(float);
                case GLTFAccessorAttributeType.VEC2:
                    return typeof(Vector2);
                case GLTFAccessorAttributeType.VEC3:
                    return typeof(Vector3);
                case GLTFAccessorAttributeType.VEC4:
                case GLTFAccessorAttributeType.MAT2:
                    return typeof(Vector4);
                case GLTFAccessorAttributeType.MAT3:
                    return typeof(Matrix4x4);
                case GLTFAccessorAttributeType.MAT4:
                default:
                    Debug.LogError("Unknown/Unsupported GLTFAccessorAttributeType");
                    return typeof(float);
            }
        }

        static Type GetAccessorComponentType( GLTFComponentType componentType ) {
            switch (componentType)
            {
                case GLTFComponentType.Byte:
                    return typeof(byte);
                case GLTFComponentType.Float:
                    return typeof(float);
                case GLTFComponentType.Short:
                    return typeof(System.Int16);
                case GLTFComponentType.UnsignedByte:
                    return typeof(byte);
                case GLTFComponentType.UnsignedInt:
                    return typeof(int);
                case GLTFComponentType.UnsignedShort:
                    return typeof(System.UInt16);
                default:
                    Debug.LogError("Unknown GLTFComponentType");
                    return null;
            }
        }
#endif // DEBUG
    }
}
