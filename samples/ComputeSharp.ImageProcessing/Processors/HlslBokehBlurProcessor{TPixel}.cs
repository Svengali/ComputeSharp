﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors;
using SixLabors.Primitives;

namespace ComputeSharp.BokehBlur.Processors
{
    /// <summary>
    /// Applies bokeh blur processing to the image
    /// </summary>
    /// <typeparam name="TPixel">The pixel format</typeparam>
    /// <remarks>This processor is based on the code from Mike Pound, see <a href="https://github.com/mikepound/convolve">github.com/mikepound/convolve</a></remarks>
    public sealed class HlslBokehBlurProcessor<TPixel> : IImageProcessor<TPixel> where TPixel : struct, IPixel<TPixel>
    {
        /// <summary>
        /// The kernel radius
        /// </summary>
        private readonly int Radius;

        /// <summary>
        /// The gamma highlight factor to use when applying the effect
        /// </summary>
        private readonly float Gamma;

        /// <summary>
        /// The maximum size of the kernel in either direction
        /// </summary>
        private readonly int KernelSize;

        /// <summary>
        /// The number of components to use when applying the bokeh blur
        /// </summary>
        private readonly int ComponentsCount;

        /// <summary>
        /// The kernel parameters to use for the current instance (a: X, b: Y, A: Z, B: W)
        /// </summary>
        private readonly Vector4[] KernelParameters;

        /// <summary>
        /// The kernel components for the current instance
        /// </summary>
        private readonly Vector2[][] Kernels;

        /// <summary>
        /// The scaling factor for kernel values
        /// </summary>
        private readonly float KernelsScale;

        /// <summary>
        /// The mapping of initialized complex kernels and parameters, to speed up the initialization of new <see cref="HlslBokehBlurProcessor{TPixel}"/> instances
        /// </summary>
        private static readonly ConcurrentDictionary<(int Radius, int ComponentsCount), (Vector4[] Parameters, float Scale, Vector2[][] Kernels)> Cache = new ConcurrentDictionary<(int, int), (Vector4[], float, Vector2[][])>();

        /// <summary>
        /// The source <see cref="Image{TPixel}"/> instance to modify
        /// </summary>
        private readonly Image<TPixel> Source;

        /// <summary>
        /// Initializes a new instance of the <see cref="HlslBokehBlurProcessor{TPixel}"/> class
        /// </summary>
        /// <param name="definition">The <see cref="HlslBokehBlurProcessor"/> defining the processor parameters</param>
        /// <param name="source">The source <see cref="Image{TPixel}"/> instance to modify</param>
        /// <param name="sourceRectangle">The source <see cref="Rectangle"/> that indicates the area to edit</param>
        public HlslBokehBlurProcessor(HlslBokehBlurProcessor definition, Image<TPixel> source, Rectangle sourceRectangle)
        {
            Radius = definition.Radius;
            KernelSize = Radius * 2 + 1;
            ComponentsCount = definition.Components;
            Gamma = definition.Gamma;
            Source = source;

            // Reuse the initialized values from the cache, if possible
            var parameters = (Radius, ComponentsCount);
            if (Cache.TryGetValue(parameters, out var info))
            {
                KernelParameters = info.Parameters;
                KernelsScale = info.Scale;
                Kernels = info.Kernels;
            }
            else
            {
                // Initialize the complex kernels and parameters with the current arguments
                (KernelParameters, KernelsScale) = GetParameters();
                Kernels = CreateComplexKernels();
                NormalizeKernels();

                // Store them in the cache for future use
                Cache.TryAdd(parameters, (KernelParameters, KernelsScale, Kernels));
            }
        }

        /// <summary>
        /// Gets the kernel scales to adjust the component values in each kernel
        /// </summary>
        private static IReadOnlyList<float> KernelScales { get; } = new[] { 1.4f, 1.2f, 1.2f, 1.2f, 1.2f, 1.2f };

        /// <summary>
        /// Gets the available bokeh blur kernel parameters
        /// </summary>
        private static IReadOnlyList<Vector4[]> KernelComponents { get; } = new[]
        {
            // 1 component
            new[] { new Vector4(0.862325f, 1.624835f, 0.767583f, 1.862321f) },

            // 2 components
            new[]
            {
                new Vector4(0.886528f, 5.268909f, 0.411259f, -0.548794f),
                new Vector4(1.960518f, 1.558213f, 0.513282f, 4.56111f)
            },

            // 3 components
            new[]
            {
                new Vector4(2.17649f, 5.043495f, 1.621035f, -2.105439f),
                new Vector4(1.019306f, 9.027613f, -0.28086f, -0.162882f),
                new Vector4(2.81511f, 1.597273f, -0.366471f, 10.300301f)
            },

            // 4 components
            new[]
            {
                new Vector4(4.338459f, 1.553635f, -5.767909f, 46.164397f),
                new Vector4(3.839993f, 4.693183f, 9.795391f, -15.227561f),
                new Vector4(2.791880f, 8.178137f, -3.048324f, 0.302959f),
                new Vector4(1.342190f, 12.328289f, 0.010001f, 0.244650f)
            },

            // 5 components
            new[]
            {
                new Vector4(4.892608f, 1.685979f, -22.356787f, 85.91246f),
                new Vector4(4.71187f, 4.998496f, 35.918936f, -28.875618f),
                new Vector4(4.052795f, 8.244168f, -13.212253f, -1.578428f),
                new Vector4(2.929212f, 11.900859f, 0.507991f, 1.816328f),
                new Vector4(1.512961f, 16.116382f, 0.138051f, -0.01f)
            },

            // 6 components
            new[]
            {
                new Vector4(5.143778f, 2.079813f, -82.326596f, 111.231024f),
                new Vector4(5.612426f, 6.153387f, 113.878661f, 58.004879f),
                new Vector4(5.982921f, 9.802895f, 39.479083f, -162.028887f),
                new Vector4(6.505167f, 11.059237f, -71.286026f, 95.027069f),
                new Vector4(3.869579f, 14.81052f, 1.405746f, -3.704914f),
                new Vector4(2.201904f, 19.032909f, -0.152784f, -0.107988f)
            }
        };

        /// <summary>
        /// Gets the kernel parameters and scaling factor for the current count value in the current instance
        /// </summary>
        private (Vector4[] Parameters, float Scale) GetParameters()
        {
            // Prepare the kernel components
            int index = Math.Max(0, Math.Min(ComponentsCount - 1, KernelComponents.Count));
            return (KernelComponents[index], KernelScales[index]);
        }

        /// <summary>
        /// Creates the collection of complex 1D kernels with the specified parameters
        /// </summary>
        private Vector2[][] CreateComplexKernels()
        {
            var kernels = new Vector2[KernelParameters.Length][];
            ref Vector4 baseRef = ref MemoryMarshal.GetReference(KernelParameters.AsSpan());
            for (int i = 0; i < KernelParameters.Length; i++)
            {
                ref Vector4 paramsRef = ref Unsafe.Add(ref baseRef, i);
                kernels[i] = CreateComplex1DKernel(paramsRef.X, paramsRef.Y);
            }

            return kernels;
        }

        /// <summary>
        /// Creates a complex 1D kernel with the specified parameters
        /// </summary>
        /// <param name="a">The exponential parameter for each complex component</param>
        /// <param name="b">The angle component for each complex component</param>
        private Vector2[] CreateComplex1DKernel(float a, float b)
        {
            var kernel = new Vector2[KernelSize];
            ref Vector2 baseRef = ref MemoryMarshal.GetReference(kernel.AsSpan());
            int r = Radius, n = -r;

            for (int i = 0; i < KernelSize; i++, n++)
            {
                // Incrementally compute the range values
                float value = n * KernelsScale * (1f / r);
                value *= value;

                // Fill in the complex kernel values
                Unsafe.Add(ref baseRef, i) = new Vector2(
                    MathF.Exp(-a * value) * MathF.Cos(b * value),
                    MathF.Exp(-a * value) * MathF.Sin(b * value));
            }

            return kernel;
        }

        /// <summary>
        /// Normalizes the kernels with respect to A * real + B * imaginary
        /// </summary>
        private void NormalizeKernels()
        {
            // Calculate the complex weighted sum
            float total = 0;
            Span<Vector2[]> kernelsSpan = Kernels.AsSpan();
            ref Vector2[] baseKernelsRef = ref MemoryMarshal.GetReference(kernelsSpan);
            ref Vector4 baseParamsRef = ref MemoryMarshal.GetReference(KernelParameters.AsSpan());

            for (int i = 0; i < KernelParameters.Length; i++)
            {
                ref Vector2[] kernelRef = ref Unsafe.Add(ref baseKernelsRef, i);
                int length = kernelRef.Length;
                ref Vector2 valueRef = ref kernelRef[0];
                ref Vector4 paramsRef = ref Unsafe.Add(ref baseParamsRef, i);

                for (int j = 0; j < length; j++)
                {
                    for (int k = 0; k < length; k++)
                    {
                        ref Vector2 jRef = ref Unsafe.Add(ref valueRef, j);
                        ref Vector2 kRef = ref Unsafe.Add(ref valueRef, k);
                        total +=
                            paramsRef.Z * (jRef.X * kRef.X - jRef.Y * kRef.Y)
                            + paramsRef.W * (jRef.X * kRef.Y + jRef.Y * kRef.X);
                    }
                }
            }

            // Normalize the kernels
            float scalar = 1f / MathF.Sqrt(total);
            for (int i = 0; i < kernelsSpan.Length; i++)
            {
                ref Vector2[] kernelsRef = ref Unsafe.Add(ref baseKernelsRef, i);
                int length = kernelsRef.Length;
                ref Vector2 valueRef = ref kernelsRef[0];

                for (int j = 0; j < length; j++)
                {
                    Unsafe.Add(ref valueRef, j) *= scalar;
                }
            }
        }

        /// <inheritdoc/>
        public void Apply()
        {
            // Preliminary gamma highlight pass
            using IMemoryOwner<Vector4> source4 = GetExposedVector4Buffer();

            using ReadOnlyBuffer<Vector4> sourceBuffer = Gpu.Default.AllocateReadOnlyBuffer(source4.Memory.Span); 
            using ReadWriteBuffer<Vector4> processingBuffer = Gpu.Default.AllocateReadWriteBuffer<Vector4>(sourceBuffer.Size);
            using ReadWriteBuffer<Vector4> firstPassBuffer = Gpu.Default.AllocateReadWriteBuffer<Vector4>(sourceBuffer.Size * 2);
            using ReadWriteBuffer<Vector4> secondPassBuffer = Gpu.Default.AllocateReadWriteBuffer<Vector4>(sourceBuffer.Size * 2);

            // Perform two 1D convolutions for each component in the current instance
            for (int i = 0; i < Kernels.Length; i++)
            {
                // Compute the resulting complex buffer for the current component
                ReadOnlyBuffer<Vector2> kernelBuffer = Gpu.Default.AllocateReadOnlyBuffer(Kernels[i]);
                ApplyVerticalConvolution(sourceBuffer, firstPassBuffer, kernelBuffer);
                ApplyHorizontalConvolution(firstPassBuffer, secondPassBuffer, kernelBuffer);

                // Add the results of the convolution with the current kernel
                Vector4 parameters = KernelParameters[i];
                SumProcessingPartials(secondPassBuffer, processingBuffer, parameters.Z, parameters.W);
            }

            // Apply the inverse gamma exposure pass, and write the final pixel data
            processingBuffer.GetData(source4.Memory.Span);
            ApplyInverseGammaExposure(source4);
        }

        /// <summary>
        /// Performs a vertical 1D complex convolution with the specified parameters
        /// </summary>
        /// <param name="source">The source <see cref="ReadOnlyBuffer{T}"/> to read data from</param>
        /// <param name="target">The target <see cref="ReadWriteBuffer{T}"/> to write the results to</param>
        /// <param name="kernel">The <see cref="ReadOnlyBuffer{T}"/> with the values for the current complex kernel</param>
        private void ApplyVerticalConvolution(
            ReadOnlyBuffer<Vector4> source,
            ReadWriteBuffer<Vector4> target,
            ReadOnlyBuffer<Vector2> kernel)
        {
            int height = Source.Height;
            int width = Source.Width;
            int maxY = height - 1;
            int maxX = width - 1;
            int kernelLength = kernel.Size;

            Gpu.Default.For(width, height, id =>
            {
                Vector4 real = Vector4.Zero;
                Vector4 imaginary = Vector4.Zero;
                int radiusY = kernelLength >> 1;
                int sourceOffsetColumnBase = id.X;

                for (int i = 0; i < kernelLength; i++)
                {
                    int offsetY = Hlsl.Clamp(id.Y + i - radiusY, 0, maxY);
                    int offsetX = Hlsl.Clamp(sourceOffsetColumnBase, 0, maxX);
                    Vector4 color = source[offsetY * width + offsetX];
                    Vector2 factors = kernel[i];

                    real += factors.X * color;
                    imaginary += factors.Y * color;
                }

                int offsetXY = id.Y * width * 2 + id.X * 2;
                target[offsetXY] = real;
                target[offsetXY + 1] = imaginary;
            });
        }

        /// <summary>
        /// Performs an horizontal 1D complex convolution with the specified parameters
        /// </summary>
        /// <param name="source">The source <see cref="ReadWriteBuffer{T}"/> to read data from</param>
        /// <param name="target">The target <see cref="ReadWriteBuffer{T}"/> to write the results to</param>
        /// <param name="kernel">The <see cref="ReadOnlyBuffer{T}"/> with the values for the current complex kernel</param>
        private void ApplyHorizontalConvolution(
            ReadWriteBuffer<Vector4> source,
            ReadWriteBuffer<Vector4> target,
            ReadOnlyBuffer<Vector2> kernel)
        {
            int height = Source.Height;
            int width = Source.Width;
            int maxY = height - 1;
            int maxX = width - 1;
            int kernelLength = kernel.Size;

            Gpu.Default.For(width, height, id =>
            {
                Vector4 real = Vector4.Zero;
                Vector4 imaginary = Vector4.Zero;
                int radiusX = kernelLength >> 1;
                int sourceOffsetColumnBase = id.X;
                int offsetY = Hlsl.Clamp(id.Y, 0, maxY);
                int offsetXY;

                for (int i = 0; i < kernelLength; i++)
                {
                    int offsetX = Hlsl.Clamp(sourceOffsetColumnBase + i - radiusX, 0, maxX);
                    offsetXY = offsetY * width * 2 + offsetX * 2;
                    Vector4 sourceReal = source[offsetXY];
                    Vector4 sourceImaginary = source[offsetXY + 1];
                    Vector2 factors = kernel[i];

                    real += factors.X * sourceReal - factors.Y * sourceImaginary;
                    imaginary += factors.X * sourceImaginary + factors.Y * sourceReal;
                }

                offsetXY = id.Y * width * 2 + id.X * 2;
                target[offsetXY] = real;
                target[offsetXY + 1] = imaginary;
            });
        }

        /// <summary>
        /// Applies the gamma correction/highlight to the input pixel buffer and returns an <see cref="IMemoryOwner{T}"/> instance with <see cref="Vector4"/> values.
        /// </summary>
        [Pure]
        private IMemoryOwner<Vector4> GetExposedVector4Buffer()
        {
            IMemoryOwner<Vector4> source4 = Source.GetConfiguration().MemoryAllocator.Allocate<Vector4>(Source.Width * Source.Height);
            float exp = Gamma;
            int width = Source.Width;

            Parallel.For(0, Source.Height, y =>
            {
                ref TPixel rPixel = ref Source.GetPixelRowSpan(y).GetPinnableReference();
                ref Vector4 r4 = ref source4.Memory.Span.Slice(y * width).GetPinnableReference();

                for (int x = 0; x < width; x++)
                {
                    ref Vector4 v = ref Unsafe.Add(ref r4, x);
                    v = Unsafe.Add(ref rPixel, x).ToVector4();
                    v.X = MathF.Pow(v.X, exp);
                    v.Y = MathF.Pow(v.Y, exp);
                    v.Z = MathF.Pow(v.Z, exp);
                }
            });

            return source4;
        }

        /// <summary>
        /// Applies the inverse gamma exposure pass to compute the final pixel values for a target image
        /// </summary>
        /// <param name="sourceValues">The source <see cref="Vector4"/> buffer to read from</param>
        private void ApplyInverseGammaExposure(IMemoryOwner<Vector4> sourceValues)
        {
            int width = Source.Width;
            float expGamma = 1 / Gamma;

            Parallel.For(0, Source.Height, y =>
            {
                Vector4 low = Vector4.Zero;
                var high = new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

                ref TPixel rPixel = ref Source.GetPixelRowSpan(y).GetPinnableReference();
                ref Vector4 r4 = ref sourceValues.Memory.Span.Slice(y * width).GetPinnableReference();

                for (int x = 0; x < width; x++)
                {
                    Vector4 v = Unsafe.Add(ref r4, x);
                    var clamp = Vector4.Clamp(v, low, high);
                    v.X = MathF.Pow(clamp.X, expGamma);
                    v.Y = MathF.Pow(clamp.Y, expGamma);
                    v.Z = MathF.Pow(clamp.Z, expGamma);

                    Unsafe.Add(ref rPixel, x).FromVector4(v);
                }
            });
        }

        /// <summary>
        /// Sums the partial results for a complex convolution pass over a single kernel component
        /// </summary>
        /// <param name="source">The source <see cref="ReadWriteBuffer{T}"/> to read data from</param>
        /// <param name="target">The target <see cref="ReadWriteBuffer{T}"/> to write the results to</param>
        /// <param name="z">The weight factor for the real component of the complex pixel values</param>
        /// <param name="w">The weight factor for the imaginary component of the complex pixel values</param>
        private void SumProcessingPartials(
            ReadWriteBuffer<Vector4> source,
            ReadWriteBuffer<Vector4> target,
            float z,
            float w)
        {
            int height = Source.Height;
            int width = Source.Width;

            Gpu.Default.For(width, height, id =>
            {
                int offsetXY = id.Y * width * 2 + id.X * 2;
                Vector4 real = source[offsetXY];
                Vector4 imaginary = source[offsetXY + 1];

                target[id.Y * width + id.X] += real * z + imaginary * w;
            });
        }

        /// <inheritdoc/>
        public void Dispose() { }
    }
}
