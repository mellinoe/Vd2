﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vulkan;
using static Vd2.Vk.VulkanUtil;
using static Vulkan.VulkanNative;

namespace Vd2.Vk
{
    internal unsafe class VkGraphicsDevice : GraphicsDevice
    {
        private static readonly FixedUtf8String s_name = "Vd2-VkGraphicsDevice";

        private VkInstance _instance;
        private VkPhysicalDevice _physicalDevice;
        private VkDeviceMemoryManager _memoryManager;
        private VkSurfaceKHR _surface;
        private VkPhysicalDeviceProperties _physicalDeviceProperties;
        private VkPhysicalDeviceFeatures _physicalDeviceFeatures;
        private VkPhysicalDeviceMemoryProperties _physicalDeviceMemProperties;
        private VkDevice _device;
        private readonly VkSwapchainFramebuffer _scFB;
        private uint _graphicsQueueIndex;
        private uint _presentQueueIndex;
        private VkCommandPool _perFrameCommandPool;
        private VkDescriptorPool _descriptorPool;
        private VkCommandPool _graphicsCommandPool;
        private VkSemaphore _imageAvailableSemaphore;
        private VkFence _imageAvailableFence;
        private RawList<VkSemaphore> _renderPassSemaphores = new RawList<VkSemaphore>();
        private VkQueue _graphicsQueue;
        private VkQueue _presentQueue;
        private VkDebugReportCallbackEXT _debugCallbackHandle;
        private PFN_vkDebugReportCallbackEXT _debugCallbackFunc;
        private readonly List<VkCommandList> _commandListsToDispose = new List<VkCommandList>();

        public override GraphicsBackend BackendType => GraphicsBackend.Vulkan;

        public VkDevice Device => _device;
        public VkPhysicalDevice PhysicalDevice => _physicalDevice;
        public VkPhysicalDeviceMemoryProperties PhysicalDeviceMemProperties => _physicalDeviceMemProperties;
        public VkQueue GraphicsQueue => _graphicsQueue;
        public uint GraphicsQueueIndex => _graphicsQueueIndex;
        public VkCommandPool GraphicsCommandPool => _graphicsCommandPool;
        public VkQueue PresentQueue => _presentQueue;
        public uint PresentQueueIndex => _presentQueueIndex;
        public VkDeviceMemoryManager MemoryManager => _memoryManager;
        public VkDescriptorPool SharedDescriptorPool => _descriptorPool;

        public VkGraphicsDevice(VkSurfaceSource surfaceSource, uint width, uint height, bool debugDevice)
        {
            CreateInstance(debugDevice);
            CreateSurface(surfaceSource);
            CreatePhysicalDevice();
            CreateLogicalDevice();
            _memoryManager = new VkDeviceMemoryManager(_device, _physicalDevice);
            ResourceFactory = new VkResourceFactory(this);
            _scFB = new VkSwapchainFramebuffer(this, _surface, width, height);
            CreatePerFrameCommandPool();
            CreateDescriptorPool();
            CreateGraphicsCommandPool();
            CreateSemaphores();

            _scFB.AcquireNextImage(_device, VkSemaphore.Null, _imageAvailableFence);
            vkWaitForFences(_device, 1, ref _imageAvailableFence, true, ulong.MaxValue);
            vkResetFences(_device, 1, ref _imageAvailableFence);

            PostContextCreated();
        }

        public override ResourceFactory ResourceFactory { get; }

        public override Framebuffer SwapchainFramebuffer => _scFB;

        public override void ExecuteCommands(CommandList cl)
        {
            VkCommandList vkCL = Util.AssertSubtype<CommandList, VkCommandList>(cl);
            VkSubmitInfo si = VkSubmitInfo.New();
            si.commandBufferCount = 1;
            VkCommandBuffer vkCB = vkCL.CommandBuffer;
            si.pCommandBuffers = &vkCB;
            VkPipelineStageFlags waitDstStageMask = VkPipelineStageFlags.ColorAttachmentOutput;
            si.pWaitDstStageMask = &waitDstStageMask;

            vkQueueSubmit(_graphicsQueue, 1, ref si, VkFence.Null);
        }

        public void EnqueueDisposedCommandBuffer(VkCommandList vkCL)
        {
            lock (_commandListsToDispose)
            {
                _commandListsToDispose.Add(vkCL);
            }
        }

        public override void ResizeMainWindow(uint width, uint height)
        {
            _scFB.Resize(width, height);
            _scFB.AcquireNextImage(_device, VkSemaphore.Null, _imageAvailableFence);
            vkWaitForFences(_device, 1, ref _imageAvailableFence, true, ulong.MaxValue);
            vkResetFences(_device, 1, ref _imageAvailableFence);
        }

        public override void SwapBuffers()
        {
            vkQueueWaitIdle(_graphicsQueue); // Meh
            FlushDestroyedCommandBuffers();

            // Then, present the swapchain.
            VkPresentInfoKHR presentInfo = VkPresentInfoKHR.New();

            VkSwapchainKHR swapchain = _scFB.Swapchain;
            presentInfo.swapchainCount = 1;
            presentInfo.pSwapchains = &swapchain;
            uint imageIndex = _scFB.ImageIndex;
            presentInfo.pImageIndices = &imageIndex;

            vkQueuePresentKHR(_presentQueue, ref presentInfo);

            _scFB.AcquireNextImage(_device, VkSemaphore.Null, _imageAvailableFence);
            vkWaitForFences(_device, 1, ref _imageAvailableFence, true, ulong.MaxValue);
            vkResetFences(_device, 1, ref _imageAvailableFence);
        }

        private void FlushDestroyedCommandBuffers()
        {
            lock (_commandListsToDispose)
            {
                foreach (VkCommandList vkCB in _commandListsToDispose)
                {
                    vkCB.DestroyCommandPool();
                }

                _commandListsToDispose.Clear();
            }
        }

        public VkCommandBuffer BeginOneTimeCommands()
        {
            VkCommandBufferAllocateInfo allocInfo = VkCommandBufferAllocateInfo.New();
            allocInfo.commandBufferCount = 1;
            allocInfo.commandPool = _graphicsCommandPool;
            allocInfo.level = VkCommandBufferLevel.Primary;

            vkAllocateCommandBuffers(_device, ref allocInfo, out VkCommandBuffer cb);

            VkCommandBufferBeginInfo beginInfo = VkCommandBufferBeginInfo.New();
            beginInfo.flags = VkCommandBufferUsageFlags.OneTimeSubmit;

            vkBeginCommandBuffer(cb, ref beginInfo);

            return cb;
        }

        public void EndOneTimeCommands(VkCommandBuffer cb, VkFence fence)
        {
            vkEndCommandBuffer(cb);

            VkSubmitInfo submitInfo = VkSubmitInfo.New();
            submitInfo.commandBufferCount = 1;
            submitInfo.pCommandBuffers = &cb;

            vkQueueSubmit(GraphicsQueue, 1, ref submitInfo, fence);
            vkQueueWaitIdle(GraphicsQueue);

            vkFreeCommandBuffers(_device, GraphicsCommandPool, 1, ref cb);
        }

        private void CreateInstance(bool debug)
        {
            HashSet<string> availableInstanceLayers = new HashSet<string>(EnumerateInstanceLayers());
            HashSet<string> availableInstanceExtensions = new HashSet<string>(EnumerateInstanceExtensions());

            VkInstanceCreateInfo instanceCI = VkInstanceCreateInfo.New();
            VkApplicationInfo applicationInfo = new VkApplicationInfo();
            applicationInfo.apiVersion = new VkVersion(1, 0, 0);
            applicationInfo.applicationVersion = new VkVersion(1, 0, 0);
            applicationInfo.engineVersion = new VkVersion(1, 0, 0);
            applicationInfo.pApplicationName = s_name;
            applicationInfo.pEngineName = s_name;

            instanceCI.pApplicationInfo = &applicationInfo;

            StackList<IntPtr, Size64Bytes> instanceExtensions = new StackList<IntPtr, Size64Bytes>();
            StackList<IntPtr, Size64Bytes> instanceLayers = new StackList<IntPtr, Size64Bytes>();

            if (!availableInstanceExtensions.Contains(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME))
            {
                throw new VdException($"The required instance extension was not available: {CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME}");
            }

            instanceExtensions.Add(CommonStrings.VK_KHR_SURFACE_EXTENSION_NAME);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!availableInstanceExtensions.Contains(CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME))
                {
                    throw new VdException($"The required instance extension was not available: {CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME}");
                }

                instanceExtensions.Add(CommonStrings.VK_KHR_WIN32_SURFACE_EXTENSION_NAME);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (!availableInstanceExtensions.Contains(CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME))
                {
                    throw new VdException($"The required instance extension was not available: {CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME}");
                }

                instanceExtensions.Add(CommonStrings.VK_KHR_XLIB_SURFACE_EXTENSION_NAME);
            }
            else
            {
                throw new NotSupportedException("This platform does not support Vulkan.");
            }

            bool debugReportExtensionAvailable = false;
            if (debug)
            {
                if (availableInstanceExtensions.Contains(CommonStrings.VK_EXT_DEBUG_REPORT_EXTENSION_NAME))
                {
                    debugReportExtensionAvailable = true;
                    instanceExtensions.Add(CommonStrings.VK_EXT_DEBUG_REPORT_EXTENSION_NAME);
                }
                if (availableInstanceLayers.Contains(CommonStrings.StandardValidationLayerName))
                {
                    instanceLayers.Add(CommonStrings.StandardValidationLayerName);
                }
            }

            instanceCI.enabledExtensionCount = instanceExtensions.Count;
            instanceCI.ppEnabledExtensionNames = (byte**)instanceExtensions.Data;

            instanceCI.enabledLayerCount = instanceLayers.Count;
            instanceCI.ppEnabledLayerNames = (byte**)instanceLayers.Data;

            VkResult result = vkCreateInstance(ref instanceCI, null, out _instance);
            CheckResult(result);

            if (debug && debugReportExtensionAvailable)
            {
                EnableDebugCallback();
            }
        }

        public void EnableDebugCallback(VkDebugReportFlagsEXT flags = VkDebugReportFlagsEXT.WarningEXT | VkDebugReportFlagsEXT.ErrorEXT)
        {
            Debug.WriteLine("Enabling Vulkan Debug callbacks.");
            _debugCallbackFunc = DebugCallback;
            IntPtr debugFunctionPtr = Marshal.GetFunctionPointerForDelegate(_debugCallbackFunc);
            VkDebugReportCallbackCreateInfoEXT debugCallbackCI = VkDebugReportCallbackCreateInfoEXT.New();
            debugCallbackCI.flags = flags;
            debugCallbackCI.pfnCallback = debugFunctionPtr;
            IntPtr createFnPtr;
            using (FixedUtf8String debugExtFnName = "vkCreateDebugReportCallbackEXT")
            {
                createFnPtr = vkGetInstanceProcAddr(_instance, debugExtFnName);
            }
            vkCreateDebugReportCallbackEXT_d createDelegate = Marshal.GetDelegateForFunctionPointer<vkCreateDebugReportCallbackEXT_d>(createFnPtr);
            VkResult result = createDelegate(_instance, &debugCallbackCI, IntPtr.Zero, out _debugCallbackHandle);
            CheckResult(result);
        }

        private uint DebugCallback(
            uint flags,
            VkDebugReportObjectTypeEXT objectType,
            ulong @object,
            UIntPtr location,
            int messageCode,
            byte* pLayerPrefix,
            byte* pMessage,
            void* pUserData)
        {
            Console.WriteLine($"[{(VkDebugReportFlagsEXT)flags}] ({objectType}) {Util.GetString(pMessage)}");
            return 0;
        }

        private void CreateSurface(VkSurfaceSource surfaceSource)
        {
            _surface = surfaceSource.CreateSurface(_instance);
        }

        private void CreatePhysicalDevice()
        {
            uint deviceCount = 0;
            vkEnumeratePhysicalDevices(_instance, ref deviceCount, null);
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No physical devices exist.");
            }

            VkPhysicalDevice[] physicalDevices = new VkPhysicalDevice[deviceCount];
            vkEnumeratePhysicalDevices(_instance, ref deviceCount, ref physicalDevices[0]);
            // Just use the first one.
            _physicalDevice = physicalDevices[0];

            vkGetPhysicalDeviceProperties(_physicalDevice, out _physicalDeviceProperties);
            string deviceName;
            fixed (byte* utf8NamePtr = _physicalDeviceProperties.deviceName)
            {
                deviceName = Encoding.UTF8.GetString(utf8NamePtr, (int)MaxPhysicalDeviceNameSize);
            }

            vkGetPhysicalDeviceFeatures(_physicalDevice, out _physicalDeviceFeatures);

            vkGetPhysicalDeviceMemoryProperties(_physicalDevice, out _physicalDeviceMemProperties);
        }

        private void CreateLogicalDevice()
        {
            GetQueueFamilyIndices();

            HashSet<uint> familyIndices = new HashSet<uint> { _graphicsQueueIndex, _presentQueueIndex };
            RawList<VkDeviceQueueCreateInfo> queueCreateInfos = new RawList<VkDeviceQueueCreateInfo>();

            foreach (uint index in familyIndices)
            {
                VkDeviceQueueCreateInfo queueCreateInfo = VkDeviceQueueCreateInfo.New();
                queueCreateInfo.queueFamilyIndex = _graphicsQueueIndex;
                queueCreateInfo.queueCount = 1;
                float priority = 1f;
                queueCreateInfo.pQueuePriorities = &priority;
                queueCreateInfos.Add(queueCreateInfo);
            }

            VkPhysicalDeviceFeatures deviceFeatures = new VkPhysicalDeviceFeatures();
            deviceFeatures.samplerAnisotropy = true;
            deviceFeatures.fillModeNonSolid = true;
            deviceFeatures.geometryShader = true;
            deviceFeatures.depthClamp = true;

            VkDeviceCreateInfo deviceCreateInfo = VkDeviceCreateInfo.New();

            fixed (VkDeviceQueueCreateInfo* qciPtr = &queueCreateInfos.Items[0])
            {
                deviceCreateInfo.pQueueCreateInfos = qciPtr;
                deviceCreateInfo.queueCreateInfoCount = queueCreateInfos.Count;

                deviceCreateInfo.pEnabledFeatures = &deviceFeatures;

                StackList<IntPtr> layerNames = new StackList<IntPtr>();
                layerNames.Add(CommonStrings.StandardValidationLayerName);
                deviceCreateInfo.enabledLayerCount = layerNames.Count;
                deviceCreateInfo.ppEnabledLayerNames = (byte**)layerNames.Data;

                byte* extensionNames = CommonStrings.VK_KHR_SWAPCHAIN_EXTENSION_NAME;
                deviceCreateInfo.enabledExtensionCount = 1;
                deviceCreateInfo.ppEnabledExtensionNames = &extensionNames;

                vkCreateDevice(_physicalDevice, ref deviceCreateInfo, null, out _device);
            }

            vkGetDeviceQueue(_device, _graphicsQueueIndex, 0, out _graphicsQueue);
            vkGetDeviceQueue(_device, _presentQueueIndex, 0, out _presentQueue);
        }

        private void GetQueueFamilyIndices()
        {
            uint queueFamilyCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, null);
            VkQueueFamilyProperties[] qfp = new VkQueueFamilyProperties[queueFamilyCount];
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, out qfp[0]);

            bool foundGraphics = false;
            bool foundPresent = false;

            for (uint i = 0; i < qfp.Length; i++)
            {
                if ((qfp[i].queueFlags & VkQueueFlags.Graphics) != 0)
                {
                    _graphicsQueueIndex = i;
                    foundGraphics = true;
                }

                vkGetPhysicalDeviceSurfaceSupportKHR(_physicalDevice, i, _surface, out VkBool32 presentSupported);
                if (presentSupported)
                {
                    _presentQueueIndex = i;
                    foundPresent = true;
                }

                if (foundGraphics && foundPresent)
                {
                    break;
                }
            }
        }

        private void CreatePerFrameCommandPool()
        {
            VkCommandPoolCreateInfo commandPoolCI = VkCommandPoolCreateInfo.New();
            commandPoolCI.flags = VkCommandPoolCreateFlags.ResetCommandBuffer | VkCommandPoolCreateFlags.Transient;
            commandPoolCI.queueFamilyIndex = _graphicsQueueIndex;
            vkCreateCommandPool(_device, ref commandPoolCI, null, out _perFrameCommandPool);
        }

        private void CreateDescriptorPool()
        {
            VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3];
            sizes[0].type = VkDescriptorType.UniformBuffer;
            sizes[0].descriptorCount = 5000;
            sizes[1].type = VkDescriptorType.SampledImage;
            sizes[1].descriptorCount = 5000;
            sizes[2].type = VkDescriptorType.Sampler;
            sizes[2].descriptorCount = 5000;

            VkDescriptorPoolCreateInfo descriptorPoolCI = VkDescriptorPoolCreateInfo.New();
            descriptorPoolCI.flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet;
            descriptorPoolCI.maxSets = 5000;
            descriptorPoolCI.pPoolSizes = sizes;
            descriptorPoolCI.poolSizeCount = 3;

            VkResult result = vkCreateDescriptorPool(_device, ref descriptorPoolCI, null, out _descriptorPool);
            CheckResult(result);
        }

        private void CreateGraphicsCommandPool()
        {
            VkCommandPoolCreateInfo commandPoolCI = VkCommandPoolCreateInfo.New();
            commandPoolCI.flags = VkCommandPoolCreateFlags.ResetCommandBuffer;
            commandPoolCI.queueFamilyIndex = _graphicsQueueIndex;
            VkResult result = vkCreateCommandPool(_device, ref commandPoolCI, null, out _graphicsCommandPool);
            CheckResult(result);
        }

        private void CreateSemaphores()
        {
            VkSemaphoreCreateInfo semaphoreCI = VkSemaphoreCreateInfo.New();
            vkCreateSemaphore(_device, ref semaphoreCI, null, out _imageAvailableSemaphore);
            const int MaxRenderPasses = 10;
            _renderPassSemaphores.Resize(MaxRenderPasses);
            for (int i = 0; i < MaxRenderPasses; i++)
            {
                vkCreateSemaphore(_device, ref semaphoreCI, null, out _renderPassSemaphores[i]);
            }

            VkFenceCreateInfo fenceCI = VkFenceCreateInfo.New();
            fenceCI.flags = VkFenceCreateFlags.None;
            vkCreateFence(_device, ref fenceCI, null, out _imageAvailableFence);
        }

        public override void Dispose()
        {
            _scFB.Dispose();
            if (_debugCallbackFunc != null)
            {
                _debugCallbackFunc = null;
                FixedUtf8String debugExtFnName = "vkDestroyDebugReportCallbackEXT";
                IntPtr destroyFuncPtr = vkGetInstanceProcAddr(_instance, debugExtFnName);
                vkDestroyDebugReportCallbackEXT_d destroyDel
                    = Marshal.GetDelegateForFunctionPointer<vkDestroyDebugReportCallbackEXT_d>(destroyFuncPtr);
                VkResult debugDestroyResult = destroyDel(_instance, _debugCallbackHandle, null);
                CheckResult(debugDestroyResult);
            }

            VkResult result = vkDeviceWaitIdle(_device);
            CheckResult(result);
            vkDestroyInstance(_instance, null);
        }

        public override void WaitForIdle()
        {
            vkQueueWaitIdle(_graphicsQueue);
        }
    }

    internal unsafe delegate VkResult vkCreateDebugReportCallbackEXT_d(
        VkInstance instance,
        VkDebugReportCallbackCreateInfoEXT* createInfo,
        IntPtr allocatorPtr,
        out VkDebugReportCallbackEXT ret);

    internal unsafe delegate VkResult vkDestroyDebugReportCallbackEXT_d(
        VkInstance instance,
        VkDebugReportCallbackEXT callback,
        VkAllocationCallbacks* pAllocator);
}
