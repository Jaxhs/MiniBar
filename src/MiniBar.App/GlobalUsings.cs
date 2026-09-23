// 什么叫 "global using"？ 普通 using 只在"当前这一个文件"里生效；加 global 前缀后，
// 这条命名空间导入对整个项目所有 .cs 文件都生效，相当于"全局预置"。
// WPF 项目几乎每个文件都要用 System / System.Windows 等，把这些高频命名空间集中写在这里，
// 其它文件就不用再写一遍 using，既整洁又不会因为漏写而编译报错。
// （注意：global using 是 C# 10 / .NET 6 起的特性，由项目文件里的 <ImplicitUsings> 或本文件启用。）
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading.Tasks;
global using System.Windows;
