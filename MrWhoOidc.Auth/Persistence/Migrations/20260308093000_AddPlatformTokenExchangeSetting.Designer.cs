using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MrWhoOidc.Auth.Persistence;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20260308093000_AddPlatformTokenExchangeSetting")]
    partial class AddPlatformTokenExchangeSetting
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.0")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("MrWhoOidc.Auth.Persistence.PlatformSettings", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<bool>("DynamicClientRegistrationEnabled")
                        .HasColumnType("boolean");

                    b.Property<bool?>("EnableTokenExchange")
                        .HasColumnType("boolean");

                    b.Property<bool>("QrLoginAtDiscoveryEnabled")
                        .HasColumnType("boolean");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("UpdatedBy")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.HasKey("Id");

                    b.ToTable("PlatformSettings");
                });
#pragma warning restore 612, 618
        }
    }
}