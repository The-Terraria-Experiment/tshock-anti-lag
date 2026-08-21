namespace AntiLag.Configuration
{
	/// <summary>How a <see cref="Curve"/> fills in the gaps between its keyframes.</summary>
	public enum InterpolationMode
	{
		/// <summary>Straight line between adjacent keyframes.</summary>
		Linear = 0,

		/// <summary>Hold the lower keyframe's value until the next keyframe is reached.</summary>
		Step = 1,

		/// <summary>Smoothstep (3t^2 - 2t^3) easing between adjacent keyframes.</summary>
		SmoothStep = 2
	}
}
