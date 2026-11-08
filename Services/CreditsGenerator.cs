using System;
using System.IO;
using System.Reflection;
using System.Text;
using ArcademiaGameLauncher.Utils;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Newtonsoft.Json.Linq;

namespace ArcademiaGameLauncher.Services
{
    public class CreditsGenerator
    {
        public static void Generate(Grid creditsPanel, Grid gifTemplateParent)
        {
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "ArcademiaGameLauncher.Assets.json.Credits.json";

            // read embedded JSON resource from assembly
            using Stream stream =
                assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException("Embedded resource not found.", resourceName);
            using StreamReader reader = new(stream, Encoding.UTF8);
            string json = reader.ReadToEnd();

            // Parse the Credits.json file and get the Credits array
            JObject creditsFile = JObject.Parse(json);
            JArray creditsArray = (JArray)creditsFile["Credits"];

            // Clear the CreditsPanel
            creditsPanel.RowDefinitions.Clear();
            creditsPanel.Children.Clear();

            // Create a new TextBlock for each credit
            for (int i = 0; i < creditsArray.Count; i++)
            {
                JObject creditsObject = (JObject)creditsArray[i];

                switch (creditsObject["Type"].ToString())
                {
                    case "Title":
                        // Create a new RowDefinition
                        RowDefinition titleRow = new() { Height = new(60, GridUnitType.Pixel) };
                        creditsPanel.RowDefinitions.Add(titleRow);

                        // Create a new Grid
                        Grid titleGrid = new()
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        Grid.SetRow(titleGrid, 2 * i);

                        // Create 2 new RowDefinitions
                        RowDefinition titleGridTitleRow = new()
                        {
                            Height = new GridLength(40, GridUnitType.Pixel),
                        };
                        titleGrid.RowDefinitions.Add(titleGridTitleRow);

                        RowDefinition titleGridSubtitleRow = new()
                        {
                            Height = new(20, GridUnitType.Pixel),
                        };
                        titleGrid.RowDefinitions.Add(titleGridSubtitleRow);

                        // Create a new TextBlock (Title)
                        TextBlock titleText = new()
                        {
                            Text = creditsObject["Value"].ToString(),
                            FontFamily = AchievementVisuals.BundledFont,
                            FontSize = 24,
                            Foreground = new SolidColorBrush(
                                Color.FromArgb(0xFF, 0xFF, 0xD9, 0x66)
                            ),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };

                        // Add the TextBlock to the Grid
                        Grid.SetRow(titleText, 0);
                        titleGrid.Children.Add(titleText);

                        // Create a new TextBlock (Subtitle)
                        if (creditsObject["Subtitle"] != null)
                        {
                            TextBlock subtitleText = new()
                            {
                                Text = creditsObject["Subtitle"].ToString(),
                                FontFamily = AchievementVisuals.BundledFont,
                                FontSize = 16,
                                Foreground = new SolidColorBrush(
                                    Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                                ),
                                HorizontalAlignment = HorizontalAlignment.Left,
                                VerticalAlignment = VerticalAlignment.Center,
                            };

                            // Add the TextBlock to the Grid
                            Grid.SetRow(subtitleText, 1);
                            titleGrid.Children.Add(subtitleText);
                        }

                        // Add the Grid to the CreditsPanel
                        creditsPanel.Children.Add(titleGrid);

                        break;
                    case "Heading":
                        // Check the Subheadings property
                        JArray subheadingsArray = (JArray)creditsObject["Subheadings"];

                        // Create a new RowDefinition
                        RowDefinition headingRow = new()
                        {
                            Height = new GridLength(
                                30 + (subheadingsArray.Count * 25),
                                GridUnitType.Pixel
                            ),
                        };
                        creditsPanel.RowDefinitions.Add(headingRow);

                        // Create a new Grid
                        Grid headingGrid = new()
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        Grid.SetRow(headingGrid, 2 * i);

                        // Create 2 new ColumnDefinitions
                        ColumnDefinition headingGridBorderColumn = new()
                        {
                            Width = new(3, GridUnitType.Pixel),
                        };
                        headingGrid.ColumnDefinitions.Add(headingGridBorderColumn);

                        ColumnDefinition headingGridContentColumn = new()
                        {
                            Width = new(1, GridUnitType.Star),
                        };
                        headingGrid.ColumnDefinitions.Add(headingGridContentColumn);

                        // Create a Grid to function as a border
                        Grid headingBorderGrid = new()
                        {
                            Background = new SolidColorBrush(
                                Color.FromArgb(0xFF, 0x33, 0x33, 0x33)
                            ),
                            Margin = new(0, 10, 0, 10),
                        };

                        // Add the Grid to the Grid
                        Grid.SetColumn(headingBorderGrid, 0);
                        headingGrid.Children.Add(headingBorderGrid);

                        // Create a new Grid to hold the Title and Subheadings
                        Grid headingContentGrid = new()
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new(25, 0, 0, 0),
                        };

                        // Add the Grid to the Grid
                        Grid.SetColumn(headingContentGrid, 1);
                        headingGrid.Children.Add(headingContentGrid);

                        // Create 2 new RowDefinitions
                        RowDefinition headingGridTitleRow = new()
                        {
                            Height = new(30, GridUnitType.Pixel),
                        };
                        headingContentGrid.RowDefinitions.Add(headingGridTitleRow);

                        RowDefinition headingGridSubheadingsRow = new()
                        {
                            Height = new(subheadingsArray.Count * 25, GridUnitType.Pixel),
                        };
                        headingContentGrid.RowDefinitions.Add(headingGridSubheadingsRow);

                        // Create a new TextBlock (Title)
                        TextBlock headingText = new()
                        {
                            Text = creditsObject["Value"].ToString(),
                            FontFamily = AchievementVisuals.BundledFont,
                            FontSize = 16,
                            Foreground = new SolidColorBrush(
                                Color.FromArgb(0xFF, 0x85, 0x8E, 0xFF)
                            ),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };

                        // Add the TextBlock to the Grid
                        Grid.SetRow(headingText, 0);
                        headingContentGrid.Children.Add(headingText);

                        // Create a new Grid for the Subheadings
                        Grid subheadingsGrid = new()
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        Grid.SetRow(subheadingsGrid, 1);

                        // For each Subheading
                        for (int j = 0; j < subheadingsArray.Count; j++)
                        {
                            // Create new RowDefinitions & for each Subheading
                            RowDefinition subheadingRow = new()
                            {
                                Height = new(25, GridUnitType.Pixel),
                            };
                            subheadingsGrid.RowDefinitions.Add(subheadingRow);

                            string colour =
                                subheadingsArray[j]["Colour"] != null
                                    ? subheadingsArray[j]["Colour"].ToString()
                                    : "White";

                            // Create a new TextBlock (Subheading)
                            TextBlock subheadingText = new()
                            {
                                Text = subheadingsArray[j]["Value"].ToString(),
                                FontFamily = AchievementVisuals.BundledFont,
                                FontSize = 18,
                                Foreground = new SolidColorBrush(
                                    Color.Parse(colour)
                                ),
                                HorizontalAlignment = HorizontalAlignment.Left,
                                VerticalAlignment = VerticalAlignment.Center,
                            };

                            // Add the TextBlock to the Grid
                            Grid.SetRow(subheadingText, j);
                            subheadingsGrid.Children.Add(subheadingText);
                        }

                        // Add the Subheading Grid to the Heading Grid
                        headingContentGrid.Children.Add(subheadingsGrid);

                        // Add the Grid to the CreditsPanel
                        creditsPanel.Children.Add(headingGrid);

                        break;
                    case "Note":
                        int noteHeight = 20 + (creditsObject["Value"].ToString().Length / 80 * 20);

                        // Create a new RowDefinition
                        RowDefinition noteRow = new()
                        {
                            Height = new(noteHeight, GridUnitType.Pixel),
                        };
                        creditsPanel.RowDefinitions.Add(noteRow);

                        // Create a new Grid
                        Grid noteGrid = new()
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new(0, 0, 100, 0),
                        };
                        Grid.SetRow(noteGrid, 2 * i);

                        // Create a new TextBlock
                        TextBlock noteText = new()
                        {
                            Text = creditsObject["Value"].ToString(),
                            FontFamily = AchievementVisuals.BundledFont,
                            FontSize = 11,
                            Foreground = new SolidColorBrush(
                                Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                            ),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 20,
                        };

                        // Add the TextBlock to the Grid
                        noteGrid.Children.Add(noteText);

                        // Add the Grid to the CreditsPanel
                        creditsPanel.Children.Add(noteGrid);

                        break;
                    case "Break":
                        // Create a new RowDefinition
                        RowDefinition breakRow = new() { Height = new(10, GridUnitType.Pixel) };
                        creditsPanel.RowDefinitions.Add(breakRow);

                        // Create a new Grid
                        Grid breakGrid = new()
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        Grid.SetRow(breakGrid, 2 * i);

                        // Create a new TextBlock
                        TextBlock breakText = new()
                        {
                            Text = "----------------------",
                            FontFamily = AchievementVisuals.BundledFont,
                            FontSize = 16,
                            Foreground = new SolidColorBrush(
                                Color.FromArgb(0xFF, 0x77, 0x77, 0x77)
                            ),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };

                        // Add the TextBlock to the Grid
                        breakGrid.Children.Add(breakText);

                        // Add the Grid to the CreditsPanel
                        creditsPanel.Children.Add(breakGrid);

                        break;
                    case "Image":
                        double overrideHeight =
                            creditsObject["HeightOverride"] != null
                                ? double.Parse(creditsObject["HeightOverride"].ToString())
                                : 100;
                        string stretch =
                            creditsObject["Stretch"] != null
                                ? creditsObject["Stretch"].ToString()
                                : "Uniform";

                        // Create a new RowDefinition
                        RowDefinition imageRow = new()
                        {
                            Height = new(overrideHeight, GridUnitType.Pixel),
                        };
                        creditsPanel.RowDefinitions.Add(imageRow);

                        // Create a new Grid
                        Grid imageGrid = new()
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        Grid.SetRow(imageGrid, 2 * i);

                        string imagePath = creditsObject["Path"].ToString();

                        // Create a new Image (Static)
                        Image imageStatic = new()
                        {
                            Source = new Bitmap(AssetLoader.Open(AchievementVisuals.Pack(imagePath.TrimStart('/')))),
                        };

                        // Set Image Stretch
                        switch (stretch)
                        {
                            case "Fill":
                                imageStatic.Stretch = Stretch.Fill;
                                break;
                            case "None":
                                imageStatic.Stretch = Stretch.None;
                                break;
                            case "Uniform":
                                imageStatic.Stretch = Stretch.Uniform;
                                break;
                            case "UniformToFill":
                                imageStatic.Stretch = Stretch.UniformToFill;
                                break;
                        }

                        // Add the Image to the Grid
                        imageGrid.Children.Add(imageStatic);

                        // Set Grid Height to Image Height
                        imageGrid.Height = overrideHeight;

                        // Create a new Image (Gif)
                        if (imagePath.EndsWith(".gif"))
                        {
                            // Copy GifTemplateElement_Parent's child element to make a new Image
                            Image imageGif = new();
                            AnimationBehavior.SetSourceUri(
                                imageGif,
                                new(imagePath, UriKind.Relative)
                            );

                            // Set Image Stretch
                            switch (stretch)
                            {
                                case "Fill":
                                    imageGif.Stretch = Stretch.Fill;
                                    break;
                                case "None":
                                    imageGif.Stretch = Stretch.None;
                                    break;
                                case "Uniform":
                                    imageGif.Stretch = Stretch.Uniform;
                                    break;
                                case "UniformToFill":
                                    imageGif.Stretch = Stretch.UniformToFill;
                                    break;
                            }

                            // Add the Image to the Grid
                            imageGrid.Children.Add(imageGif);

                            AnimationBehavior.AddLoadedHandler(
                                imageGif,
                                (sender, e) =>
                                {
                                    // Hide the static image
                                    imageStatic.IsVisible = false;
                                }
                            );
                        }

                        // Add the Grid to the CreditsPanel
                        creditsPanel.Children.Add(imageGrid);

                        break;
                }

                // Create a space between each credit
                if (i < creditsArray.Count - 1)
                {
                    // Create a new RowDefinition
                    RowDefinition spaceRow = new() { Height = new(40, GridUnitType.Pixel) };
                    creditsPanel.RowDefinitions.Add(spaceRow);
                }
            }
        }
    }
}
