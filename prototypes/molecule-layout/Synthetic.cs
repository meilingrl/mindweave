namespace MoleculeLayout;

/// <summary>Made-up workspaces, larger than the owner's, with fixed seeds so every run draws the same pictures.</summary>
public static class Synthetic
{
    private static readonly string[] Titles =
    [
        "傅里叶变换的直观理解", "卷积与相关的区别", "采样定理为什么成立", "窗函数怎么选", "小波和傅里叶比较",
        "梯度下降为什么有效", "学习率调度策略", "动量法的物理直觉", "Adam 的偏差修正", "过拟合与正则化",
        "注意力机制的来历", "位置编码的几种做法", "Transformer 的残差连接", "层归一化放在哪里", "多头注意力的意义",
        "蒸汽机的热效率", "铁路标准轨距的由来", "早期信号系统", "英国铁路国有化", "高铁与磁悬浮比较",
        "光圈快门感光度", "曝光三角形", "景深怎么控制", "人像构图要点", "夜景长曝光",
        "螃蟹为什么横着走", "甲壳类的蜕壳过程", "蓝色血液的成分", "帝王蟹其实是寄居蟹", "蟹黄是什么",
        "Why does SGD generalize", "Kalman filter intuition", "Bayes rule in one picture", "What is a monad",
        "Rust ownership rules", "Lifetimes explained", "Borrow checker errors", "CAP theorem tradeoffs",
        "数据库索引的原理", "B 树与 B+ 树", "事务隔离级别", "MVCC 是怎么实现的", "分库分表什么时候需要",
        "量子纠缠是什么", "贝尔不等式", "退相干", "薛定谔的猫到底说明了什么", "多世界诠释",
        "罗马帝国为什么衰落", "拜占庭的延续", "十字军东征的起因", "威尼斯的商业共和国", "黑死病对欧洲的影响",
        "咖啡因的作用机制", "睡眠周期", "褪黑素", "昼夜节律", "午睡多久合适",
        "API 分页与增量采集", "爬虫的礼貌规则", "CDC 与消息队列", "数据湖与数仓", "OLAP 术语解释",
    ];

    private sealed class Lcg(ulong seed)
    {
        private ulong state = seed * 6364136223846793005UL + 1442695040888963407UL;

        public double Next()
        {
            state = (state * 6364136223846793005UL) + 1442695040888963407UL;
            return (state >> 11) * (1.0 / (1UL << 53));
        }

        public int Next(int max) => (int)(Next() * max);
    }

    public static IEnumerable<Workspace> All()
    {
        yield return Topics("S1 six topics", 11, [14, 10, 8, 7, 5, 4], chain: 0.45);
        yield return Topics("S2 one deep exploration", 12, [40], chain: 0.55);
        yield return Fan("S3 wide fan", 13);
        yield return Comb("S4 comb", 15);
        yield return Topics("S5 fifteen topics", 14, [30, 22, 18, 15, 12, 12, 10, 9, 8, 8, 7, 6, 5, 4, 3], chain: 0.45);
    }

    private static Workspace Topics(string name, ulong seed, int[] sizes, double chain)
    {
        var rng = new Lcg(seed);
        var dots = new List<Dot>();
        long time = 1_760_000_000_000;
        var title = (int)(seed * 7);
        foreach (var size in sizes)
        {
            time += 86_400_000L * (1 + rng.Next(2));
            var ids = new List<string>();
            for (var k = 0; k < size; k++)
            {
                string? parent = null;
                if (k > 0)
                {
                    // Explorations mostly continue from the latest branch; sometimes they go back to an earlier one.
                    parent = rng.Next() < chain ? ids[^1] : ids[rng.Next(Math.Min(ids.Count, 1 + (ids.Count / 2)))];
                }

                var id = $"{name}-{dots.Count:D4}";
                ids.Add(id);
                time += 600_000;
                dots.Add(new Dot
                {
                    Id = id, ParentId = parent, Created = time, Title = Titles[title++ % Titles.Length],
                    Questions = 1 + rng.Next(6), HasSummary = rng.Next() < 0.5,
                });
            }
        }

        return new Workspace { Name = name, Dots = dots };
    }

    private static Workspace Fan(string name, ulong seed)
    {
        var rng = new Lcg(seed);
        var dots = new List<Dot>();
        long time = 1_760_000_000_000;
        var t = 3;
        Dot Add(string? parent)
        {
            var dot = new Dot
            {
                Id = $"f{dots.Count:D3}", ParentId = parent, Created = time += 600_000, Title = Titles[t++ % Titles.Length],
                Questions = 1 + rng.Next(5), HasSummary = rng.Next() < 0.5,
            };
            dots.Add(dot);
            return dot;
        }

        var root = Add(null);
        for (var k = 0; k < 12; k++)
        {
            var child = Add(root.Id);
            var kids = k % 4 == 0 ? 3 : k % 3 == 0 ? 2 : k % 2 == 0 ? 1 : 0;
            for (var j = 0; j < kids; j++)
            {
                Add(child.Id);
            }
        }

        return new Workspace { Name = name, Dots = dots };
    }

    private static Workspace Comb(string name, int length)
    {
        var dots = new List<Dot>();
        long time = 1_760_000_000_000;
        var t = 20;
        string? previous = null;
        for (var k = 0; k < length; k++)
        {
            var spine = new Dot { Id = $"c{k:D3}", ParentId = previous, Created = time += 600_000, Title = Titles[t++ % Titles.Length], Questions = 2, HasSummary = k % 2 == 0 };
            dots.Add(spine);
            dots.Add(new Dot { Id = $"c{k:D3}x", ParentId = spine.Id, Created = time += 600_000, Title = Titles[t++ % Titles.Length], Questions = 1 });
            previous = spine.Id;
        }

        return new Workspace { Name = name, Dots = dots };
    }
}
