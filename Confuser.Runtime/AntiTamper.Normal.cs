using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Confuser.Runtime {
	internal static class AntiTamperNormal {
		[DllImport("kernel32.dll")]
		static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

		static unsafe void Initialize() {
			Module m = typeof(AntiTamperNormal).Module;
			var b = (byte*)Marshal.GetHINSTANCE(m);
			if (b == (byte*)(-1) || b == null)
				return;
			byte* p = b + *(uint*)(b + 0x3c);
			ushort s = *(ushort*)(p + 0x6);
			ushort o = *(ushort*)(p + 0x14);

			// 判断映像是按节虚拟地址布局的真映像，还是按文件布局的扁平副本。
			// 不能用 Module.FullyQualifiedName[0] 判：单文件 bundle 加载的程序集恒为 "<Unknown>"，
			// 但本保护自己插入的密文节带 IMAGE_SCN_MEM_WRITE，CoreCLR 会把这种程序集转成真正的
			// 内存映像（节在 VirtualAddress、内存私有可写）；只有不带可写节的产物才是只读的扁平
			// 文件映射（节在 PointerToRawData）。把真映像误判成扁平副本会让解密写到错的地址。
			// 注意：判定必须内联在本方法里，不能拆成同类型的独立方法 —— 实测在"单文件 + anti dump"
			// 组合下，多出的那个方法会在解密完成前被 JIT，抛 BadImageFormatException。
			var sec = (uint*)(p + 0x18 + o);
			bool mapped = false;
			for (int i = 0; i < s; i++) {
				if ((sec[i * 0xa + 9] & 0x80000000) != 0) { //IMAGE_SCN_MEM_WRITE
					mapped = true;
					break;
				}
			}
			if (!mapped) {
				string name = m.FullyQualifiedName;
				mapped = name.Length > 0 && name[0] != '<';
			}
			bool f = !mapped;

			uint* e = null;
			uint l = 0;
			var r = (uint*)(p + 0x18 + o);
			uint z = (uint)Mutation.KeyI1, x = (uint)Mutation.KeyI2, c = (uint)Mutation.KeyI3, v = (uint)Mutation.KeyI4;
			uint fb = (uint)Mutation.KeyI5;
			for (int i = 0; i < s; i++) {
				uint g = (*r++) * (*r++);
				if (g == (uint)Mutation.KeyI0) {
					e = (uint*)(b + (f ? *(r + 3) : *(r + 1)));
					l = (f ? *(r + 2) : *(r + 0)) >> 2;
				}
				else if (g != 0) {
					var q = (uint*)(b + (f ? *(r + 3) : *(r + 1)));
					uint j = *(r + 2) >> 2;
					for (uint k = 0; k < j; k++) {
						uint t = (z ^ (*q++)) + x + c * v;
						z = x;
						x = c;
						x = v;
						v = t;
					}
				}
				r += 8;
			}

			uint[] y = new uint[0x10], d = new uint[0x10];
			for (int i = 0; i < 0x10; i++) {
				y[i] = v;
				d[i] = x;
				z = (x >> Mutation.KeyI6) | (x << (32 - Mutation.KeyI6));
				x = (c >> Mutation.KeyI7) | (c << (32 - Mutation.KeyI7));
				c = (v >> Mutation.KeyI8) | (v << (32 - Mutation.KeyI8));
				v = (z >> Mutation.KeyI9) | (z << (32 - Mutation.KeyI9));
			}
			Mutation.Crypt(y, d);

			uint w;
			if (!VirtualProtect((IntPtr)e, l << 2, 0x40, out w))
				return; //拿不到写权限（例如单文件下只读的文件映射视图）→ 放弃，写只读页会崩

			if (w == 0x40)
				return; //本来就可写可执行，说明密文节此前已解密过，重复解密会把方法体弄坏

			uint h = 0;
			for (uint i = 0; i < l; i++) {
				*e ^= y[h & 0xf];
				y[h & 0xf] = (y[h & 0xf] ^ (*e++)) + fb;
				h++;
			}
		}
	}
}
