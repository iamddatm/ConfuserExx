using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Confuser.Runtime {
	internal static class AntiDump {
		[DllImport("kernel32.dll")]
		static extern unsafe bool VirtualProtect(byte* lpAddress, int dwSize, uint flNewProtect, out uint lpflOldProtect);

		[StructLayout(LayoutKind.Sequential)]
		struct MemoryBasicInformation {
			public IntPtr BaseAddress;
			public IntPtr AllocationBase;
			public uint AllocationProtect;
			public IntPtr RegionSize;
			public uint State;
			public uint Protect;
			public uint Type;
		}

		[DllImport("kernel32.dll")]
		static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, IntPtr dwLength);

		/// <summary>
		///     判断 [address, address + size) 是否落在已提交且可读的内存中。
		///     单文件发布下 <c>Marshal.GetHINSTANCE</c> 可能返回 -1（该程序集没有独立的 HINSTANCE），
		///     按未知地址解引用会直接 ACCESS_VIOLATION，所以取值前必须先过这里。
		/// </summary>
		/// <param name="address">待检查的内存地址。</param>
		/// <param name="size">从该地址起需要可读的字节数。</param>
		/// <returns>该区间是否可安全读取。</returns>
		static unsafe bool Readable(byte* address, int size) {
			MemoryBasicInformation info;
			if (VirtualQuery((IntPtr)address, out info, (IntPtr)Marshal.SizeOf(typeof(MemoryBasicInformation))) == IntPtr.Zero)
				return false;
			if (info.State != 0x1000) //MEM_COMMIT
				return false;
			if ((info.Protect & 0x01) != 0 || (info.Protect & 0x100) != 0) //PAGE_NOACCESS / PAGE_GUARD
				return false;
			return (long)address + size <= (long)info.BaseAddress + info.RegionSize.ToInt64();
		}

		/// <summary>
		///     判断当前模块在内存里是按节虚拟地址布局的真映像（<c>true</c>），还是按文件布局的扁平副本（<c>false</c>）。
		/// </summary>
		/// <remarks>
		///     不能用 <c>Module.FullyQualifiedName[0] == '&lt;'</c> 判：单文件 bundle 加载的程序集恒为
		///     <c>"&lt;Unknown&gt;"</c>，但它们的实际布局并不相同——CoreCLR 对 bundle 内程序集只做只读文件映射，
		///     <b>只有当映像里存在可写节时</b>才会把它转成真正的内存映像（节在 VirtualAddress、内存私有可写）。
		///     anti tamper 的密文节与 anti dump 自己插入的可写节都会命中这条规则，所以必须按"有无可写节"判，
		///     否则会把真映像当成扁平副本、按文件偏移去寻址。
		/// </remarks>
		/// <param name="module">当前模块，仅用于无文件路径时的兜底判断。</param>
		/// <param name="sectionTable">节表首地址。</param>
		/// <param name="sectionCount">节的数量。</param>
		/// <returns>是否为真映像。</returns>
		static unsafe bool IsMappedImage(Module module, byte* sectionTable, int sectionCount) {
			for (int i = 0; i < sectionCount; i++) {
				if ((*(uint*)(sectionTable + i * 0x28 + 0x24) & 0x80000000) != 0) //IMAGE_SCN_MEM_WRITE
					return true;
			}

			// 兜底：能从磁盘路径加载的程序集（非单文件）一定是真映像。
			string name = module.FullyQualifiedName;
			return name.Length > 0 && name[0] != '<';
		}

		static unsafe void Initialize() {
			uint old;
			Module module = typeof(AntiDump).Module;
			var bas = (byte*)Marshal.GetHINSTANCE(module);
			if (bas == (byte*)(-1) || bas == null)
				return;
			if (!Readable(bas, 0x40))
				return;

			byte* ptr = bas + 0x3c;
			ptr = bas + *(uint*)ptr;
			ptr += 0x6;
			ushort sectNum = *(ushort*)ptr;
			ptr += 14;
			ushort optSize = *(ushort*)ptr;
			ptr = ptr + 0x4 + optSize; //节表起点
			if (!Readable(bas, (int)(ptr - bas) + sectNum * 0x28))
				return;

			// 单文件 bundle 里不带可写节的产物是只读的文件映射，VirtualProtect 必然失败，
			// 而下面的擦除不检查返回值、直接写只读页 —— 那就是单文件下的 ACCESS_VIOLATION。
			// 先用 PE 头页探一次可写性，探不到就整体放弃（这一层保护失效，但不让进程崩）。
			if (!VirtualProtect(bas, 0x1000, 0x40, out old))
				return;

			byte* @new = stackalloc byte[11];
			if (IsMappedImage(module, ptr, sectNum)) //Mapped
			{
				//VirtualProtect(ptr - 16, 8, 0x40, out old);
				//*(uint*)(ptr - 12) = 0;
				byte* mdDir = bas + *(uint*)(ptr - 16);
				//*(uint*)(ptr - 16) = 0;

				if (*(uint*)(ptr - 0x78) != 0) {
					byte* importDir = bas + *(uint*)(ptr - 0x78);
					byte* oftMod = bas + *(uint*)importDir;
					byte* modName = bas + *(uint*)(importDir + 12);
					byte* funcName = bas + *(uint*)oftMod + 2;
					VirtualProtect(modName, 11, 0x40, out old);

					*(uint*)@new = 0x6c64746e;
					*((uint*)@new + 1) = 0x6c642e6c;
					*((ushort*)@new + 4) = 0x006c;
					*(@new + 10) = 0;

					for (int i = 0; i < 11; i++)
						*(modName + i) = *(@new + i);

					VirtualProtect(funcName, 11, 0x40, out old);

					*(uint*)@new = 0x6f43744e;
					*((uint*)@new + 1) = 0x6e69746e;
					*((ushort*)@new + 4) = 0x6575;
					*(@new + 10) = 0;

					for (int i = 0; i < 11; i++)
						*(funcName + i) = *(@new + i);
				}

				for (int i = 0; i < sectNum; i++) {
					VirtualProtect(ptr, 8, 0x40, out old);
					Marshal.Copy(new byte[8], 0, (IntPtr)ptr, 8);
					ptr += 0x28;
				}
				VirtualProtect(mdDir, 0x48, 0x40, out old);
				byte* mdHdr = bas + *(uint*)(mdDir + 8);
				*(uint*)mdDir = 0;
				*((uint*)mdDir + 1) = 0;
				*((uint*)mdDir + 2) = 0;
				*((uint*)mdDir + 3) = 0;

				VirtualProtect(mdHdr, 4, 0x40, out old);
				*(uint*)mdHdr = 0;
				mdHdr += 12;
				mdHdr += *(uint*)mdHdr;
				mdHdr = (byte*)(((ulong)mdHdr + 7) & ~3UL);
				mdHdr += 2;
				ushort numOfStream = *mdHdr;
				mdHdr += 2;
				for (int i = 0; i < numOfStream; i++) {
					VirtualProtect(mdHdr, 8, 0x40, out old);
					//*(uint*)mdHdr = 0;
					mdHdr += 4;
					//*(uint*)mdHdr = 0;
					mdHdr += 4;
					for (int ii = 0; ii < 8; ii++) {
						VirtualProtect(mdHdr, 4, 0x40, out old);
						*mdHdr = 0;
						mdHdr++;
						if (*mdHdr == 0) {
							mdHdr += 3;
							break;
						}
						*mdHdr = 0;
						mdHdr++;
						if (*mdHdr == 0) {
							mdHdr += 2;
							break;
						}
						*mdHdr = 0;
						mdHdr++;
						if (*mdHdr == 0) {
							mdHdr += 1;
							break;
						}
						*mdHdr = 0;
						mdHdr++;
					}
				}
			}
			else //Flat
			{
				//VirtualProtect(ptr - 16, 8, 0x40, out old);
				//*(uint*)(ptr - 12) = 0;
				uint mdDir = *(uint*)(ptr - 16);
				//*(uint*)(ptr - 16) = 0;
				uint importDir = *(uint*)(ptr - 0x78);

				var vAdrs = new uint[sectNum];
				var vSizes = new uint[sectNum];
				var rAdrs = new uint[sectNum];
				for (int i = 0; i < sectNum; i++) {
					VirtualProtect(ptr, 8, 0x40, out old);
					Marshal.Copy(new byte[8], 0, (IntPtr)ptr, 8);
					vAdrs[i] = *(uint*)(ptr + 12);
					vSizes[i] = *(uint*)(ptr + 8);
					rAdrs[i] = *(uint*)(ptr + 20);
					ptr += 0x28;
				}


				if (importDir != 0) {
					for (int i = 0; i < sectNum; i++)
						if (vAdrs[i] <= importDir && importDir < vAdrs[i] + vSizes[i]) {
							importDir = importDir - vAdrs[i] + rAdrs[i];
							break;
						}
					byte* importDirPtr = bas + importDir;
					uint oftMod = *(uint*)importDirPtr;
					for (int i = 0; i < sectNum; i++)
						if (vAdrs[i] <= oftMod && oftMod < vAdrs[i] + vSizes[i]) {
							oftMod = oftMod - vAdrs[i] + rAdrs[i];
							break;
						}
					byte* oftModPtr = bas + oftMod;
					uint modName = *(uint*)(importDirPtr + 12);
					for (int i = 0; i < sectNum; i++)
						if (vAdrs[i] <= modName && modName < vAdrs[i] + vSizes[i]) {
							modName = modName - vAdrs[i] + rAdrs[i];
							break;
						}
					uint funcName = *(uint*)oftModPtr + 2;
					for (int i = 0; i < sectNum; i++)
						if (vAdrs[i] <= funcName && funcName < vAdrs[i] + vSizes[i]) {
							funcName = funcName - vAdrs[i] + rAdrs[i];
							break;
						}
					VirtualProtect(bas + modName, 11, 0x40, out old);

					*(uint*)@new = 0x6c64746e;
					*((uint*)@new + 1) = 0x6c642e6c;
					*((ushort*)@new + 4) = 0x006c;
					*(@new + 10) = 0;

					for (int i = 0; i < 11; i++)
						*(bas + modName + i) = *(@new + i);

					VirtualProtect(bas + funcName, 11, 0x40, out old);

					*(uint*)@new = 0x6f43744e;
					*((uint*)@new + 1) = 0x6e69746e;
					*((ushort*)@new + 4) = 0x6575;
					*(@new + 10) = 0;

					for (int i = 0; i < 11; i++)
						*(bas + funcName + i) = *(@new + i);
				}


				for (int i = 0; i < sectNum; i++)
					if (vAdrs[i] <= mdDir && mdDir < vAdrs[i] + vSizes[i]) {
						mdDir = mdDir - vAdrs[i] + rAdrs[i];
						break;
					}
				byte* mdDirPtr = bas + mdDir;
				VirtualProtect(mdDirPtr, 0x48, 0x40, out old);
				uint mdHdr = *(uint*)(mdDirPtr + 8);
				for (int i = 0; i < sectNum; i++)
					if (vAdrs[i] <= mdHdr && mdHdr < vAdrs[i] + vSizes[i]) {
						mdHdr = mdHdr - vAdrs[i] + rAdrs[i];
						break;
					}
				*(uint*)mdDirPtr = 0;
				*((uint*)mdDirPtr + 1) = 0;
				*((uint*)mdDirPtr + 2) = 0;
				*((uint*)mdDirPtr + 3) = 0;


				byte* mdHdrPtr = bas + mdHdr;
				VirtualProtect(mdHdrPtr, 4, 0x40, out old);
				*(uint*)mdHdrPtr = 0;
				mdHdrPtr += 12;
				mdHdrPtr += *(uint*)mdHdrPtr;
				mdHdrPtr = (byte*)(((ulong)mdHdrPtr + 7) & ~3UL);
				mdHdrPtr += 2;
				ushort numOfStream = *mdHdrPtr;
				mdHdrPtr += 2;
				for (int i = 0; i < numOfStream; i++) {
					VirtualProtect(mdHdrPtr, 8, 0x40, out old);
					//*(uint*)mdHdrPtr = 0;
					mdHdrPtr += 4;
					//*(uint*)mdHdrPtr = 0;
					mdHdrPtr += 4;
					for (int ii = 0; ii < 8; ii++) {
						VirtualProtect(mdHdrPtr, 4, 0x40, out old);
						*mdHdrPtr = 0;
						mdHdrPtr++;
						if (*mdHdrPtr == 0) {
							mdHdrPtr += 3;
							break;
						}
						*mdHdrPtr = 0;
						mdHdrPtr++;
						if (*mdHdrPtr == 0) {
							mdHdrPtr += 2;
							break;
						}
						*mdHdrPtr = 0;
						mdHdrPtr++;
						if (*mdHdrPtr == 0) {
							mdHdrPtr += 1;
							break;
						}
						*mdHdrPtr = 0;
						mdHdrPtr++;
					}
				}
			}
		}
	}
}
