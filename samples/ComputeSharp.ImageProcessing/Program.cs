using System;
using System.IO;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ComputeSharp.BokehBlur.Processors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors;

namespace ComputeSharp.ImageProcessing
{
	public class Program
	{


		static public void Main()
		{
			BenchmarkRunner.Run<BokehTest>();
		}
																						



		static void StepThrough()
		{
			Console.WriteLine( ">> Loading image" );
			string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "city.jpg");
			using Image<Rgb24> image = Image.Load<Rgb24>(path);

			// Apply a series of processors and save the results
			foreach( var effect in new (string Name, IImageProcessor Processor)[]
			{
								("bokeh", new HlslBokehBlurProcessor(80, 2, 3)),
								("gaussian", new HlslGaussianBlurProcessor(80))
			} )
			{
				Console.WriteLine( $">> Applying {effect.Name}" );
				using Image<Rgb24> copy = image.Clone();
				copy.Mutate( c => c.ApplyProcessor( effect.Processor ) );

				Console.WriteLine( $">> Saving {effect.Name} to disk" );
				string targetPath = Path.Combine(
										Path.GetRelativePath(Path.GetDirectoryName(path), @"..\..\..\"),
										$"{Path.GetFileNameWithoutExtension(path)}_{effect.Name}{Path.GetExtension(path)}");
				copy.Save( targetPath );
			}
		}
	}



	public class BokehTest
	{
		[Params(32, 80)] public int Radius;

		private Image<Rgba32> Image1;

		private Image<Rgba32> Image2;

		[GlobalSetup]
		public void Setup()
		{
			string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "city.jpg");
			using Image<Rgba32> image = Image.Load<Rgba32>(path);
			Image1 = image.Clone();
			Image2 = image.Clone();

			image.Mutate( c => c.ApplyProcessor( new HlslBokehBlurProcessor( 3, 1, 1 ) ) ); // Compile the HLSL shader
		}

		[GlobalCleanup]
		public void Cleanup()
		{
			Image1.Dispose();
			Image2.Dispose();
		}

		[Benchmark]
		public void Cpu() => Image1.Mutate( c => c.BokehBlur( Radius, 2, 3 ) );

		[Benchmark]
		public void Gpu() => Image2.Mutate( c => c.ApplyProcessor( new HlslBokehBlurProcessor( Radius, 2, 3 ) ) );
	}

}
